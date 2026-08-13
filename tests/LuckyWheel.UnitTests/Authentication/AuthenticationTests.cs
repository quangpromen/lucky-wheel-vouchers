using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Infrastructure.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LuckyWheel.UnitTests.Authentication;

public sealed class AuthenticationTests
{
    [Fact]
    public void PasswordHasher_VerifiesCorrectPassword_AndRejectsWrongPassword()
    {
        var admin = NewAdmin();
        var hasher = new PasswordHasher<AdminUser>();
        var hash = hasher.HashPassword(admin, "Correct-password-123!");

        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(admin, hash, "Correct-password-123!"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(admin, hash, "wrong"));
    }

    [Fact]
    public void JwtGenerator_EmitsRequiredClaimsAndClockBasedExpiration()
    {
        var now = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
        var options = Options.Create(new JwtOptions
        {
            Issuer = "issuer", Audience = "audience", AccessTokenLifetimeMinutes = 30,
            SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        });
        var generated = new JwtTokenGenerator(options, new FixedClock(now)).Generate(NewAdmin());
        var token = new JwtSecurityTokenHandler().ReadJwtToken(generated.AccessToken);

        Assert.Equal(now.AddMinutes(30), generated.ExpiresAtUtc);
        Assert.Contains(token.Claims, x => x.Type == JwtRegisteredClaimNames.Sub);
        Assert.Contains(token.Claims, x => x.Type == JwtRegisteredClaimNames.UniqueName && x.Value == "admin@example.com");
        Assert.Contains(token.Claims, x => x.Type == ClaimTypes.Role && x.Value == "Admin");
        Assert.Contains(token.Claims, x => x.Type == JwtRegisteredClaimNames.Jti);
    }

    private static AdminUser NewAdmin() => new("admin@example.com", "Administrator", DateTime.UtcNow);
    private sealed class FixedClock(DateTimeOffset value) : IClock { public DateTimeOffset UtcNow => value; }
}
