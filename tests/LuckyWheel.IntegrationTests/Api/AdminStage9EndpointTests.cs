using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LuckyWheel.Api.Controllers;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Features.Admin;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Domain.Enums;
using LuckyWheel.IntegrationTests.Api.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LuckyWheel.IntegrationTests.Api;

public sealed class AdminStage9EndpointMetadataTests
{
    [Fact]
    public void AdminPrizeKeysController_RequiresAdminOnlyPolicy()
    {
        var authorize = Assert.Single(typeof(AdminPrizeKeysController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("AdminOnly", authorize.Policy);
    }

    [Fact]
    public void AdminWheelsController_RequiresAdminOnlyPolicy()
    {
        var authorize = Assert.Single(typeof(AdminWheelsController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("AdminOnly", authorize.Policy);
    }

    [Fact]
    public void Stage9Actions_DoNotExposeAnonymous()
    {
        var methods = typeof(AdminPrizeKeysController).GetMethods()
            .Concat(typeof(AdminWheelsController).GetMethods());

        Assert.Empty(methods.SelectMany(x => x.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)));
    }
}

public sealed class AdminStage9AuthorizationTests : IClassFixture<AdminAuthTestFactory>
{
    private readonly HttpClient _client;

    public AdminStage9AuthorizationTests(AdminAuthTestFactory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData("/api/admin/prize-keys/00000000-0000-0000-0000-000000000001/redeem", "POST")]
    [InlineData("/api/admin/prize-keys/00000000-0000-0000-0000-000000000001/cancel", "POST")]
    [InlineData("/api/admin/prize-keys/00000000-0000-0000-0000-000000000001/replacement", "POST")]
    [InlineData("/api/admin/prize-keys/expire-overdue", "POST")]
    [InlineData("/api/admin/wheels/00000000-0000-0000-0000-000000000001/spins", "GET")]
    public async Task Stage9Endpoints_WithoutToken_ReturnUnauthorized(string path, string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { reason = "Hủy quà", note = "Giao quà" });
        }

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public sealed class AdminStage9ApiBehaviorTests : IClassFixture<AdminStage9TestFactory>
{
    private readonly AdminStage9TestFactory _factory;
    private readonly HttpClient _client;

    public AdminStage9ApiBehaviorTests(AdminStage9TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Stage9Endpoint_AuthenticatedWithoutAdminRole_ReturnsForbidden()
    {
        await _factory.GetAdminTokenAsync(_client);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_factory.State.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            _factory.State.Issuer,
            _factory.State.Audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, _factory.State.AdminId!.Value.ToString()),
                new Claim(ClaimTypes.Role, "Viewer")
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/prize-keys/{Guid.NewGuid()}/redeem");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        request.Content = JsonContent.Create(new { note = "Đổi quà" });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RedeemKey_AsAdmin_ReturnsRedeemResponse()
    {
        var token = await _factory.GetAdminTokenAsync(_client);
        var prizeKeyId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/prize-keys/{prizeKeyId}/redeem");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { note = "Đã giao voucher" });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(prizeKeyId.ToString(), json.GetProperty("prizeKeyId").GetString());
        Assert.Equal("Redeemed", json.GetProperty("status").GetString());
        Assert.Equal("Đã giao voucher", json.GetProperty("note").GetString());
    }

    [Fact]
    public async Task CancelKey_AsAdmin_ReturnsCancelResponse()
    {
        var token = await _factory.GetAdminTokenAsync(_client);
        var prizeKeyId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/prize-keys/{prizeKeyId}/cancel");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { reason = "Thông tin không hợp lệ" });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(prizeKeyId.ToString(), json.GetProperty("prizeKeyId").GetString());
        Assert.Equal("Cancelled", json.GetProperty("status").GetString());
        Assert.Equal("Thông tin không hợp lệ", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CreateReplacementKey_AsAdmin_ReturnsReplacementResponse()
    {
        var token = await _factory.GetAdminTokenAsync(_client);
        var originalKeyId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/prize-keys/{originalKeyId}/replacement");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(originalKeyId.ToString(), json.GetProperty("originalPrizeKeyId").GetString());
        Assert.Equal("Assigned", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("newPrizeKeyId", out _));
    }

    [Fact]
    public async Task ExpireOverdue_AsAdmin_ReturnsExpireResult()
    {
        var token = await _factory.GetAdminTokenAsync(_client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/prize-keys/expire-overdue?batchSize=50");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.True(json.TryGetProperty("processedCount", out _));
        Assert.True(json.TryGetProperty("successCount", out _));
    }

    [Fact]
    public async Task GetWheelSpins_AsAdmin_ReturnsMaskedEmailAndNoSensitiveData()
    {
        var token = await _factory.GetAdminTokenAsync(_client);
        var wheelId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/wheels/{wheelId}/spins?pageNumber=1&pageSize=20");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(1, json.GetProperty("totalCount").GetInt32());
        var items = json.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());

        var first = items[0];
        Assert.Equal("q***v@gmail.com", first.GetProperty("maskedEmail").GetString());
        Assert.Equal("Win", first.GetProperty("result").GetString());

        // Ensure NO secret data
        Assert.False(first.TryGetProperty("code", out _));
        Assert.False(first.TryGetProperty("codeHash", out _));
        Assert.False(first.TryGetProperty("codeEncrypted", out _));
        Assert.False(first.TryGetProperty("encryptedCode", out _));
        Assert.False(first.TryGetProperty("idempotencyKey", out _));
        Assert.False(first.TryGetProperty("receiptToken", out _));
    }
}

public sealed class AdminStage9TestFactory : WebApplicationFactory<Program>
{
    public AdminAuthState State { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = State.Issuer,
                ["Jwt:Audience"] = State.Audience,
                ["Jwt:AccessTokenLifetimeMinutes"] = "5",
                ["Jwt:SigningKey"] = State.SigningKey
            });
        });

        builder.ConfigureServices(services =>
        {
            services.Configure<HealthCheckServiceOptions>(options => options.Registrations.Clear());
            services.RemoveAll<IAdminAuthenticationService>();
            services.AddSingleton(State);
            services.AddScoped<IAdminAuthenticationService, TestAdminAuthenticationService>();
            services.RemoveAll<IPrizeKeyService>();
            services.AddScoped<IPrizeKeyService, TestPrizeKeyService>();
            services.RemoveAll<IPrizeKeyExpirationProcessor>();
            services.AddScoped<IPrizeKeyExpirationProcessor, TestPrizeKeyExpirationProcessor>();

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.ValidIssuer = State.Issuer;
                options.TokenValidationParameters.ValidAudience = State.Audience;
                options.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(State.SigningKey));
            });
        });
    }

    public async Task<string> GetAdminTokenAsync(HttpClient client)
    {
        var loginResponse = await client.PostAsJsonAsync("/api/admin/auth/login", new
        {
            username = "admin@example.test",
            password = State.ValidPassword
        });

        var json = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync()).RootElement;
        return json.GetProperty("accessToken").GetString()!;
    }

    private sealed class TestPrizeKeyService : IPrizeKeyService
    {
        public Task<GeneratePrizeKeysResponse> GenerateKeysAsync(Guid prizeId, GeneratePrizeKeysRequest request, System.Threading.CancellationToken ct)
            => Task.FromResult(new GeneratePrizeKeysResponse(prizeId, request.Quantity, "Available", DateTime.UtcNow));

        public Task<PageResult<PrizeKeyDto>> GetKeysAsync(int pageNumber, int pageSize, Guid? prizeId, PrizeKeyStatus? status, string? code = null, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new PageResult<PrizeKeyDto>([], pageNumber, pageSize, 0));

        public Task<PrizeKeyDto> GetKeyByIdAsync(Guid prizeKeyId, System.Threading.CancellationToken ct)
            => Task.FromResult(new PrizeKeyDto(prizeKeyId, Guid.NewGuid(), "Voucher 100k", "LW-TEST-1234-5678-ABCD", "Available", DateTime.UtcNow, null, null, null, null, null));

        public Task<RedeemPrizeKeyResponse> RedeemKeyAsync(Guid prizeKeyId, RedeemPrizeKeyRequest request, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new RedeemPrizeKeyResponse(prizeKeyId, Guid.NewGuid(), "Redeemed", DateTime.UtcNow, request?.Note));

        public Task<CancelPrizeKeyResponse> CancelKeyAsync(Guid prizeKeyId, CancelPrizeKeyRequest request, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new CancelPrizeKeyResponse(prizeKeyId, Guid.NewGuid(), "Cancelled", DateTime.UtcNow, request?.Reason ?? ""));

        public Task<CreateReplacementPrizeKeyResponse> CreateReplacementKeyAsync(Guid prizeKeyId, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new CreateReplacementPrizeKeyResponse(Guid.NewGuid(), prizeKeyId, Guid.NewGuid(), Guid.NewGuid(), "Assigned", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), "LW-REPL-1234-5678-ABCD"));

        public Task<PageResult<AdminSpinHistoryDto>> GetWheelSpinsAsync(Guid wheelId, AdminSpinHistoryFilter filter, System.Threading.CancellationToken ct = default)
        {
            var items = new List<AdminSpinHistoryDto>
            {
                new(
                    Guid.NewGuid(),
                    wheelId,
                    Guid.NewGuid(),
                    1,
                    "q***v@gmail.com",
                    "Win",
                    "Completed",
                    Guid.NewGuid(),
                    "Voucher 500k",
                    Guid.NewGuid(),
                    "Assigned",
                    DateTime.UtcNow,
                    null,
                    null)
            };
            return Task.FromResult(new PageResult<AdminSpinHistoryDto>(items, filter.PageNumber, filter.PageSize, 1));
        }
    }

    private sealed class TestPrizeKeyExpirationProcessor : IPrizeKeyExpirationProcessor
    {
        public Task<ExpirePrizeKeysResult> ProcessExpiredKeysBatchAsync(int batchSize, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new ExpirePrizeKeysResult(1, 1, 0, [Guid.NewGuid()]));
    }
}
