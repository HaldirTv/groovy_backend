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
    [ProducesResponseType(typeof(PagedResultDto<TrackDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetLibrary(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Потрібна авторизація." });

        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        try
        {
            var (items, totalCount) = await _libraryService.GetUserLibraryAsync(userId, baseUrl, pageNumber, pageSize, cancellationToken);
            return Ok(new PagedResultDto<TrackDto>(items, totalCount, pageNumber, pageSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while fetching library for user {UserId}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "An unexpected error occurred." });
        }
    }
}