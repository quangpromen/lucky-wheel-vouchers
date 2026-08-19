using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using LuckyWheel.Application.Common.Authentication;
using Microsoft.AspNetCore.Http;

namespace LuckyWheel.Infrastructure.Authentication;

public sealed class CurrentAdminContext(IHttpContextAccessor httpContextAccessor) : ICurrentAdminContext
{
    public Guid? AdminId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            var sub = user?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                   ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    public bool IsAuthenticated => httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true;
}
