using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Features.Public;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LuckyWheel.Api.Controllers;

[ApiController]
[Route("api/public/wheels")]
[AllowAnonymous]
public class PublicWheelsController(IPublicWheelService publicWheelService) : ControllerBase
{
    /// <summary>
    /// Retrieves active configuration and wheel entries for a wheel by slug.
    /// </summary>
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(PublicWheelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicWheelDto>> GetWheel(string slug, CancellationToken ct)
    {
        var wheel = await publicWheelService.GetActiveWheelBySlugAsync(slug, ct);
        return Ok(wheel);
    }

    /// <summary>
    /// Executes a lucky wheel spin for a user with their Gmail address and idempotency key.
    /// </summary>
    [HttpPost("{slug}/spins")]
    [EnableRateLimiting("PublicSpinRateLimit")]
    [ProducesResponseType(typeof(SpinResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SpinResultDto>> Spin(
        string slug,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] SpinRequest request,
        CancellationToken ct)
    {
        var result = await publicWheelService.SpinAsync(slug, idempotencyKey, request, ct);
        return Ok(result);
    }
}
