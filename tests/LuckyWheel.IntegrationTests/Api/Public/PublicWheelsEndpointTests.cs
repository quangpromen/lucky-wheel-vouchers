using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Features.Public;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LuckyWheel.IntegrationTests.Api.Public;

public sealed class PublicWheelTestFactory : WebApplicationFactory<Program>
{
    public sealed class TestPublicWheelService : IPublicWheelService
    {
        public Task<PublicWheelDto> GetActiveWheelBySlugAsync(string slug, CancellationToken ct = default)
        {
            if (slug == "active-wheel")
            {
                var entries = new List<PublicWheelEntryDto>
                {
                    new(Guid.NewGuid(), 1, "Voucher 10%", "#FFAA00", null, false),
                    new(Guid.NewGuid(), 2, "Chúc bạn may mắn lần sau", "#CCCCCC", null, true)
                };
                return Task.FromResult(new PublicWheelDto(Guid.NewGuid(), "Active Wheel", "active-wheel", Guid.NewGuid(), 1, entries));
            }

            throw NotFoundException.WithCode("WHEEL_NOT_AVAILABLE", "Wheel is not available.");
        }

        public Task<SpinResultDto> SpinAsync(string slug, string? idempotencyKeyHeader, SpinRequest request, CancellationToken ct = default)
        {
            PublicSpinValidation.ValidateSpinRequest(idempotencyKeyHeader, request);

            if (slug != "active-wheel")
                throw NotFoundException.WithCode("WHEEL_NOT_AVAILABLE", "Wheel is not available.");

            if (PublicSpinValidation.NormalizeEmail(request.Email!) == "alreadywon@gmail.com")
                throw new ConflictException("EMAIL_ALREADY_WON", "This email has already won.");

            if (idempotencyKeyHeader == "reused-conflict-key")
                throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "Key reused with different email.");

            return Task.FromResult(new SpinResultDto(
                Guid.NewGuid(),
                "RC-TEST-TOKEN",
                "NoPrize",
                Guid.NewGuid(),
                2,
                "Chúc bạn may mắn lần sau",
                null,
                DateTime.UtcNow));
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.Configure<HealthCheckServiceOptions>(options => options.Registrations.Clear());
            services.RemoveAll<IPublicWheelService>();
            services.AddScoped<IPublicWheelService, TestPublicWheelService>();
        });
    }
}

public sealed class PublicWheelsEndpointTests(PublicWheelTestFactory factory) : IClassFixture<PublicWheelTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetWheel_ActiveWheel_Returns200WithEntriesWithoutWeightsOrKeys()
    {
        var response = await _client.GetAsync("/api/public/wheels/active-wheel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Active Wheel", doc.GetProperty("name").GetString());
        Assert.Equal("active-wheel", doc.GetProperty("slug").GetString());
        Assert.Equal(1, doc.GetProperty("versionNumber").GetInt32());

        var entries = doc.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());

        // Ensure weights, probabilities, keys or ciphertext are not exposed
        var entry0 = entries[0];
        Assert.False(entry0.TryGetProperty("weight", out _));
        Assert.False(entry0.TryGetProperty("probabilityWeight", out _));
        Assert.False(entry0.TryGetProperty("key", out _));
        Assert.False(entry0.TryGetProperty("codeHash", out _));
    }

    [Fact]
    public async Task GetWheel_UnavailableWheel_Returns404ProblemDetailsWithWheelNotAvailableCode()
    {
        var response = await _client.GetAsync("/api/public/wheels/non-existing-wheel");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("WHEEL_NOT_AVAILABLE", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Spin_MissingIdempotencyKey_Returns400ValidationError()
    {
        var request = new SpinRequest("user@gmail.com", true);
        var response = await _client.PostAsJsonAsync("/api/public/wheels/active-wheel/spins", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Spin_InvalidEmailDomain_Returns400ValidationError()
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/public/wheels/active-wheel/spins")
        {
            Content = JsonContent.Create(new SpinRequest("user@yahoo.com", true))
        };
        httpRequest.Headers.Add("Idempotency-Key", "valid-key-123");

        var response = await _client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Spin_TermsNotAccepted_Returns400ValidationError()
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/public/wheels/active-wheel/spins")
        {
            Content = JsonContent.Create(new SpinRequest("user@gmail.com", false))
        };
        httpRequest.Headers.Add("Idempotency-Key", "valid-key-123");

        var response = await _client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", problem.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("alreadywon@gmail.com")]
    [InlineData("  Already.Won+promo@GMAIL.COM  ")]
    public async Task Spin_EmailAlreadyWon_Returns409ProblemDetailsWithEmailAlreadyWonCode(string email)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/public/wheels/active-wheel/spins")
        {
            Content = JsonContent.Create(new SpinRequest(email, true))
        };
        httpRequest.Headers.Add("Idempotency-Key", "valid-key-123");

        var response = await _client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EMAIL_ALREADY_WON", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Spin_IdempotencyKeyReused_Returns409ProblemDetailsWithIdempotencyKeyReusedCode()
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/public/wheels/active-wheel/spins")
        {
            Content = JsonContent.Create(new SpinRequest("user@gmail.com", true))
        };
        httpRequest.Headers.Add("Idempotency-Key", "reused-conflict-key");

        var response = await _client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Spin_ValidRequest_Returns200SuccessResult()
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/public/wheels/active-wheel/spins")
        {
            Content = JsonContent.Create(new SpinRequest("user@gmail.com", true))
        };
        httpRequest.Headers.Add("Idempotency-Key", "valid-key-999");

        var response = await _client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NoPrize", doc.GetProperty("result").GetString());
        Assert.Equal("RC-TEST-TOKEN", doc.GetProperty("receiptToken").GetString());
    }
}

public sealed class PublicWheelsGmailValidationEndpointTests(PublicWheelTestFactory factory) : IClassFixture<PublicWheelTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("user@googlemail.com")]
    [InlineData(".user@gmail.com")]
    [InlineData("user.@gmail.com")]
    [InlineData("user..name@gmail.com")]
    [InlineData("+tag@gmail.com")]
    [InlineData("user+@gmail.com")]
    [InlineData("user name@gmail.com")]
    [InlineData("\u212A@gmail.com")]
    public async Task Spin_InvalidGmailVariants_Returns400ValidationError(string invalidEmail)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/public/wheels/active-wheel/spins")
        {
            Content = JsonContent.Create(new SpinRequest(invalidEmail, true))
        };
        httpRequest.Headers.Add("Idempotency-Key", "valid-key-gmail-1");

        var response = await _client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Spin_ValidGmailWithDotsAndPlusTag_Returns200SuccessResult()
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/public/wheels/active-wheel/spins")
        {
            Content = JsonContent.Create(new SpinRequest("  Quang.Dev+campaign@GMAIL.COM  ", true))
        };
        httpRequest.Headers.Add("Idempotency-Key", "valid-key-gmail-2");

        var response = await _client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NoPrize", doc.GetProperty("result").GetString());
    }
}
