using System.Security.Claims;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuckyWheel.Api.Controllers;

[ApiController]
[Route("api/admin/auth")]
public sealed class AdminAuthController(IAdminAuthenticationService authenticationService) : ControllerBase
{
    /// <summary>Đăng nhập Admin và nhận JWT access token.</summary>
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(AdminLoginRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var username = request.Username?.Trim() ?? string.Empty;
        if (username.Length is < 1 or > 320) errors["username"] = ["Username is required and must not exceed 320 characters."];
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024) errors["password"] = ["Password is required and must not exceed 1024 characters."];
        if (errors.Count > 0) throw new ValidationException(errors);

        var result = await authenticationService.LoginAsync(username, request.Password!, cancellationToken);
        if (result is null) return UnauthorizedProblem("INVALID_CREDENTIALS", "Invalid username or password.");
        return Ok(new { result.AccessToken, TokenType = "Bearer", result.ExpiresAtUtc, result.Admin });
    }

    /// <summary>Lấy thông tin Admin đang đăng nhập và kiểm tra token.</summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var idValue = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var id))
            return UnauthorizedProblem("UNAUTHORIZED", "Authentication is required.");
        var admin = await authenticationService.GetActiveAdminAsync(id, cancellationToken);
        return admin is null ? UnauthorizedProblem("UNAUTHORIZED", "Authentication is required.") : Ok(admin);
    }

    private ObjectResult UnauthorizedProblem(string code, string detail)
    {
        var problem = new ProblemDetails { Status = 401, Title = "Unauthorized", Detail = detail, Instance = Request.Path };
        problem.Extensions["errorCode"] = code;
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return StatusCode(401, problem);
    }
}

public sealed record AdminLoginRequest(string? Username, string? Password);
