using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

/// <summary>
/// Handles audio track uploads for the Groovra Music Microservice.
/// Base route: /music/upload
/// </summary>
[ApiController]
[Route("music/[controller]")]
[Produces("application/json")]
public class UploadController : ControllerBase
{
    private readonly UploadService _uploadService;
    private readonly ILogger<UploadController> _logger;

    public UploadController(UploadService uploadService, ILogger<UploadController> logger)
    {
        _uploadService = uploadService;
        _logger = logger;
    }

    /// <summary>
    /// POST /music/upload/track
    /// Uploads an audio track (multipart/form-data).
    /// Fields: Title*, ArtistName*, Album?, Genre?, File*, CoverImage?
    /// </summary>
    /// <response code="201">Track uploaded successfully.</response>
    /// <response code="400">Validation failed (bad MIME type, file too large, missing fields, etc.).</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("track")]
    [RequestSizeLimit(220_000_000)]          // 220 MB hard limit for the whole request
    [RequestFormLimits(MultipartBodyLengthLimit = 220_000_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadTrackResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UploadTrack(
        [FromForm] UploadTrackRequestDto dto,
        CancellationToken cancellationToken)
    {
        // ── Basic model validation ──────────────────────────────────────────
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (string.IsNullOrWhiteSpace(dto.Title))
            return BadRequest(new { Error = "Title is required." });

        if (string.IsNullOrWhiteSpace(dto.ArtistName))
            return BadRequest(new { Error = "ArtistName is required." });

        if (dto.File is null || dto.File.Length == 0)
            return BadRequest(new { Error = "An audio file must be provided." });

        try
        {
            var track = await _uploadService.UploadTrackAsync(dto, cancellationToken);

            // Build public-facing URLs.
            // In production these would be CDN URLs; for now they are local API routes.
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var audioUrl = $"{baseUrl}/music/files/audio/{track.Id}{System.IO.Path.GetExtension(track.AudioRelativePath)}";
            var coverUrl = track.CoverImageRelativePath is not null
                ? $"{baseUrl}/music/files/covers/{track.Id}_cover{System.IO.Path.GetExtension(track.CoverImageRelativePath)}"
                : null;

            var response = new UploadTrackResponseDto
            {
                TrackId = track.Id,
                Title = track.Title,
                ArtistName = track.ArtistName,
                Album = track.Album,
                Genre = track.Genre,
                DurationSeconds = track.DurationSeconds,
                FileSizeBytes = track.FileSizeBytes,
                ContentType = track.ContentType,
                AudioUrl = audioUrl,
                CoverImageUrl = coverUrl,
                UploadedAt = track.UploadedAt,
            };

            _logger.LogInformation("Track uploaded. Id={TrackId}, Title={Title}", track.Id, track.Title);

            return CreatedAtAction(
                actionName: nameof(UploadTrack),
                routeValues: new { id = track.Id },
                value: response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Upload validation failed: {Message}", ex.Message);
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during track upload.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { Error = "An unexpected error occurred. Please try again later." });
        }
    }
}
