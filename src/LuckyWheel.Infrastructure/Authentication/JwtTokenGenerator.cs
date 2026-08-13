using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LuckyWheel.Infrastructure.Authentication;

public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly JwtOptions _options;
    private readonly IClock _clock;

    public JwtTokenGenerator(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public GeneratedAccessToken Generate(AdminUser adminUser)
    {
        var now = _clock.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenLifetimeMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, adminUser.Id.ToString()),
            new(ClaimTypes.NameIdentifier, adminUser.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, adminUser.Email),
            new(ClaimTypes.Name, adminUser.DisplayName),
            new(ClaimTypes.Role, "Admin"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var token = new JwtSecurityToken(_options.Issuer, _options.Audience, claims,
            now.UtcDateTime, expires.UtcDateTime, new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new GeneratedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
