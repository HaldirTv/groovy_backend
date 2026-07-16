using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/[controller]")]
[Produces("application/json")]
public class LibraryController : ControllerBase
{
    private readonly LibraryService _libraryService;
    private readonly ILogger<LibraryController> _logger;

    public LibraryController(LibraryService libraryService, ILogger<LibraryController> logger)
    {
        _libraryService = libraryService;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TrackDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetLibrary(CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Потрібна авторизація." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        try
        {
            var tracks = await _libraryService.GetUserLibraryAsync(userId, baseUrl, cancellationToken);
            return Ok(tracks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while fetching library for user {UserId}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "An unexpected error occurred." });
        }
    }
}