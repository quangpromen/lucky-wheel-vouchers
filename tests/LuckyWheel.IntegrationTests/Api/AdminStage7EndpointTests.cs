using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
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

public sealed class AdminStage7EndpointMetadataTests
{
    [Theory]
    [InlineData(typeof(AdminPrizeKeysController))]
    [InlineData(typeof(AdminWheelVersionsController))]
    [InlineData(typeof(AdminPrizesController))]
    [InlineData(typeof(AdminWheelsController))]
    public void Stage7Controllers_RequireAdminOnlyPolicy(Type controller)
    {
        var authorize = Assert.Single(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("AdminOnly", authorize.Policy);
    }

    [Theory]
    [InlineData(typeof(AdminPrizeKeysController))]
    [InlineData(typeof(AdminWheelVersionsController))]
    [InlineData(typeof(AdminPrizesController))]
    [InlineData(typeof(AdminWheelsController))]
    public void Stage7Controllers_DoNotExposeAnonymousActions(Type controller)
    {
        Assert.Empty(controller.GetMethods().SelectMany(x => x.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)));
    }
}

public sealed class AdminStage7AuthorizationTests : IClassFixture<AdminAuthTestFactory>
{
    private readonly HttpClient _client;

    public AdminStage7AuthorizationTests(AdminAuthTestFactory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData("/api/admin/prizes/00000000-0000-0000-0000-000000000001/keys/generate", "POST")]
    [InlineData("/api/admin/prize-keys", "GET")]
    [InlineData("/api/admin/prize-keys/00000000-0000-0000-0000-000000000001", "GET")]
    [InlineData("/api/admin/wheel-versions/00000000-0000-0000-0000-000000000001/activate", "POST")]
    [InlineData("/api/admin/wheel-versions/00000000-0000-0000-0000-000000000001/close", "POST")]
    public async Task Stage7Endpoints_WithoutToken_ReturnUnauthorized(string path, string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { quantity = 10, rowVersion = Convert.ToBase64String(new byte[8]) });
        }

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public sealed class AdminStage7ApiBehaviorTests : IClassFixture<AdminStage7TestFactory>
{
    private readonly AdminStage7TestFactory _factory;
    private readonly HttpClient _client;

    public AdminStage7ApiBehaviorTests(AdminStage7TestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GenerateKeys_AsAdmin_ReturnsResponseWithoutSecrets()
    {
        var token = await _factory.GetAdminTokenAsync(_client);
        var prizeId = Guid.NewGuid();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/prizes/{prizeId}/keys/generate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { quantity = 50 });

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(prizeId.ToString(), json.GetProperty("prizeId").GetString());
        Assert.Equal(50, json.GetProperty("generatedCount").GetInt32());
        Assert.Equal("Available", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("createdAtUtc", out _));

        // Ensure NO secret or key codes are returned
        Assert.False(json.TryGetProperty("code", out _));
        Assert.False(json.TryGetProperty("codeHash", out _));
        Assert.False(json.TryGetProperty("codeEncrypted", out _));
        Assert.False(json.TryGetProperty("encryptedCode", out _));
        Assert.False(json.TryGetProperty("encryptionNonce", out _));
        Assert.False(json.TryGetProperty("encryptionTag", out _));
        Assert.False(json.TryGetProperty("plaintext", out _));
    }

    [Fact]
    public async Task Stage7Endpoint_AuthenticatedWithoutAdminRole_ReturnsForbidden()
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
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/prize-keys");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListKeys_AsAdmin_ReturnsMetadataWithDecryptedCode()
    {
        var token = await _factory.GetAdminTokenAsync(_client);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/prize-keys?pageNumber=1&pageSize=10");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.True(json.TryGetProperty("items", out var items));
        Assert.True(json.TryGetProperty("totalCount", out _));
        Assert.True(json.TryGetProperty("page", out _));
        Assert.True(json.TryGetProperty("pageSize", out _));

        foreach (var item in items.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("id", out _));
            Assert.True(item.TryGetProperty("prizeId", out _));
            Assert.True(item.TryGetProperty("prizeName", out _));
            Assert.True(item.TryGetProperty("code", out var codeProp));
            Assert.Equal("LW-TEST-1234-5678-ABCD", codeProp.GetString());
            Assert.True(item.TryGetProperty("status", out _));
            Assert.True(item.TryGetProperty("createdAtUtc", out _));

            // Verify no secret crypto storage fields
            Assert.False(item.TryGetProperty("codeHash", out _));
            Assert.False(item.TryGetProperty("codeEncrypted", out _));
            Assert.False(item.TryGetProperty("encryptedCode", out _));
            Assert.False(item.TryGetProperty("encryptionNonce", out _));
            Assert.False(item.TryGetProperty("encryptionTag", out _));
            Assert.False(item.TryGetProperty("nonce", out _));
            Assert.False(item.TryGetProperty("tag", out _));
        }
    }

    [Fact]
    public async Task PublicEndpoints_RemainPublic()
    {
        var health = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var info = await _client.GetAsync("/api/system/info");
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
    }
}

public sealed class AdminStage7TestFactory : WebApplicationFactory<Program>
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
        {
            return Task.FromResult(new GeneratePrizeKeysResponse(prizeId, request.Quantity, "Available", DateTime.UtcNow));
        }

        public Task<PageResult<PrizeKeyDto>> GetKeysAsync(int pageNumber, int pageSize, Guid? prizeId, PrizeKeyStatus? status, string? code = null, System.Threading.CancellationToken ct = default)
        {
            var items = new List<PrizeKeyDto>
            {
                new(Guid.NewGuid(), prizeId ?? Guid.NewGuid(), "Voucher 100k", "LW-TEST-1234-5678-ABCD", "Available", DateTime.UtcNow, null, null, null, null, null)
            };
            return Task.FromResult(new PageResult<PrizeKeyDto>(items, pageNumber, pageSize, 1));
        }

        public Task<PrizeKeyDto> GetKeyByIdAsync(Guid prizeKeyId, System.Threading.CancellationToken ct)
        {
            return Task.FromResult(new PrizeKeyDto(prizeKeyId, Guid.NewGuid(), "Voucher 100k", "LW-TEST-1234-5678-ABCD", "Available", DateTime.UtcNow, null, null, null, null, null));
        }
    }
}
