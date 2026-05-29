using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

/// <summary>
/// Управление треками: получение списка, удаление, переименование.
/// Base route: /music/tracks
/// </summary>
[ApiController]
[Route("music/[controller]")]
[Produces("application/json")]
public class TracksController : ControllerBase
{
    private readonly MusicService _musicService;
    private readonly ILogger<TracksController> _logger;

    public TracksController(MusicService musicService, ILogger<TracksController> logger)
    {
        _musicService = musicService;
        _logger = logger;
    }

    // ─── GET /music/tracks ────────────────────────────────────────────────────

    /// <summary>
    /// GET /music/tracks
    /// Возвращает список всех загруженных треков (новые — первыми).
    /// </summary>
    /// <response code="200">Список треков (может быть пустым).</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TrackDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllTracks(CancellationToken cancellationToken)
    {
        var tracks = await _musicService.GetAllTracksAsync(cancellationToken);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var result = tracks.Select(t => MapToDto(t, baseUrl)).ToList();

        return Ok(result);
    }

    // ─── DELETE /music/tracks/{id} ────────────────────────────────────────────

    /// <summary>
    /// DELETE /music/tracks/{id}
    /// Удаляет трек и связанные медиафайлы с диска.
    /// </summary>
    /// <param name="id">GUID трека.</param>
    /// <response code="204">Трек успешно удалён.</response>
    /// <response code="404">Трек с указанным id не найден.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTrack(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await _musicService.DeleteTrackAsync(id, cancellationToken);

        if (!deleted)
            return NotFound(new { Error = $"Трек с id '{id}' не найден." });

        return NoContent();
    }

    // ─── PATCH /music/tracks/{id}/title ──────────────────────────────────────

    /// <summary>
    /// PATCH /music/tracks/{id}/title
    /// Изменяет название трека.
    /// </summary>
    /// <param name="id">GUID трека.</param>
    /// <param name="dto">Новое название.</param>
    /// <response code="200">Трек успешно переименован, возвращает обновлённый объект.</response>
    /// <response code="400">Некорректное тело запроса.</response>
    /// <response code="404">Трек с указанным id не найден.</response>
    [HttpPatch("{id:guid}/title")]
    [ProducesResponseType(typeof(TrackDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RenameTrack(
        Guid id,
        [FromBody] RenameTrackRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var track = await _musicService.RenameTrackAsync(id, dto.Title, cancellationToken);

        if (track is null)
            return NotFound(new { Error = $"Трек с id '{id}' не найден." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(MapToDto(track, baseUrl));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static TrackDto MapToDto(Track track, string baseUrl)
    {
        var audioExt = Path.GetExtension(track.AudioRelativePath);
        var audioUrl = $"{baseUrl}/music/files/audio/{track.Id}{audioExt}";

        string? coverUrl = null;
        if (track.CoverImageRelativePath is not null)
        {
            var coverExt = Path.GetExtension(track.CoverImageRelativePath);
            coverUrl = $"{baseUrl}/music/files/covers/{track.Id}_cover{coverExt}";
        }

        return new TrackDto
        {
            TrackId       = track.Id,
            Title         = track.Title,
            ArtistName    = track.ArtistName,
            Album         = track.Album,
            Genre         = track.Genre,
            DurationSeconds = track.DurationSeconds,
            FileSizeBytes = track.FileSizeBytes,
            ContentType   = track.ContentType,
            AudioUrl      = audioUrl,
            CoverImageUrl = coverUrl,
            UploadedAt    = track.UploadedAt,
        };
    }
}
