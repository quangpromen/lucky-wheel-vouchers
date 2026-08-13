using System.Security.Cryptography;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Infrastructure.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace LuckyWheel.IntegrationTests.Api.Authentication;

public sealed class AdminAuthTestFactory : WebApplicationFactory<Program>
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
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.ValidIssuer = State.Issuer;
                options.TokenValidationParameters.ValidAudience = State.Audience;
                options.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(State.SigningKey));
            });
        });
    }

    public string CreateExpiredToken()
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var token = new JwtSecurityToken(
            State.Issuer,
            State.Audience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-10),
            expires: DateTime.UtcNow.AddMinutes(-1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(State.SigningKey)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class AdminAuthState
{
    public bool IsActive { get; set; } = true;
    public Guid? AdminId { get; set; }
    public string Issuer { get; } = "LuckyWheel.Tests";
    public string Audience { get; } = "LuckyWheel.Tests";
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    public string ValidPassword { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    public string InvalidPassword { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
}

internal sealed class TestAdminAuthenticationService(AdminAuthState state, IClock clock)
    : IAdminAuthenticationService
{
    private const string Username = "admin@example.test";
    private const string DisplayName = "Administrator";

    public Task<AdminLoginResult?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (!state.IsActive || username != Username || password != state.ValidPassword)
            return Task.FromResult<AdminLoginResult?>(null);

        var admin = new AdminUser(Username, DisplayName, DateTime.UtcNow);
        state.AdminId = admin.Id;
        var token = new JwtTokenGenerator(Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            Issuer = state.Issuer,
            Audience = state.Audience,
            AccessTokenLifetimeMinutes = 5,
            SigningKey = state.SigningKey
        }), clock).Generate(admin);
        return Task.FromResult<AdminLoginResult?>(new(token.AccessToken, token.ExpiresAtUtc,
            new AdminIdentity(admin.Id, Username, DisplayName)));
    }

    public Task<AdminIdentity?> GetActiveAdminAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<AdminIdentity?>(state.IsActive
            ? new AdminIdentity(id, Username, DisplayName)
            : null);
}
