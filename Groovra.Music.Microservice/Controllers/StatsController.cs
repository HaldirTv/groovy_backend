using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/stats")]
public class StatsController : ControllerBase
{
    private readonly StatsService _statsService;

    public StatsController(StatsService statsService)
    {
        _statsService = statsService;
    }

    // GET music/stats/me
    [HttpGet("me")]
    public async Task<IActionResult> GetMyStats(CancellationToken cancellationToken)
    {

        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

 
        var userRole = Request.Headers["X-User-Role"].ToString();
        if (!userRole.HasRole(AppRoles.Artist))
            return StatusCode(StatusCodes.Status403Forbidden, 
                new { message = "Тільки артисти мають доступ до дашборду аналітики." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

      
        var result = await _statsService.GetArtistDashboardStatsAsync(currentUserId, baseUrl, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

    // GET music/stats/global — публично доступно (гостям тоже)
    [HttpGet("global")]
    public async Task<IActionResult> GetGlobalStats(CancellationToken cancellationToken)
    {
        var result = await _statsService.GetGlobalStatsAsync(cancellationToken);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }
}