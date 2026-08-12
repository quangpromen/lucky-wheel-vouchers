using Microsoft.AspNetCore.Mvc;

namespace LuckyWheel.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;

    public SystemController(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    /// <summary>
    /// Returns basic application information for system health verification.
    /// </summary>
    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        return Ok(new
        {
            application = "Lucky Wheel API",
            version = "v1",
            environment = _environment.EnvironmentName
        });
    }
}
