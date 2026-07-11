using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Result;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/playlists")]
public class PlaylistsController : ControllerBase
{
    private readonly PlaylistService _playlistService;

    public PlaylistsController(PlaylistService playlistService)
    {
        _playlistService = playlistService;
    }

    // POST music/playlists
    [HttpPost]
    public async Task<IActionResult> CreatePlaylist(
        [FromBody] CreatePlaylistDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var result = await _playlistService.CreatePlaylistAsync(
            userId, dto.Title, dto.Description, GetBaseUrl(),dto.IsPrivate, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

    // GET music/playlists
    [HttpGet]
    public async Task<IActionResult> GetPlaylists(
        [FromQuery] Guid? targetUserId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var target = targetUserId ?? requesterId;
        
        // ПРОВЕРКА ДОСТУПА В КОНТРОЛЛЕРЕ:
        // Если юзер смотрит свои плейлисты или он Админ - показываем всё (включая приватные).
        // Если смотрит чужие - только публичные.
        bool includePrivate = (target == requesterId) || HttpContext.UserIsInRole(AppRoles.Admin);

        var result = await _playlistService.GetUserPlaylistsAsync(target, includePrivate, GetBaseUrl(), cancellationToken);

        return Ok(result.Data);
    }

    // GET music/playlists/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetPlaylist(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var playlist = await _playlistService.GetRawPlaylistAsync(id, cancellationToken);
        if (playlist == null)
            return NotFound(new { message = "Плейлист не знайдено." });

        // ПРОВЕРКА ДОСТУПА В КОНТРОЛЛЕРЕ:
        if (playlist.IsPrivate && playlist.UserId != requesterId && !HttpContext.UserIsInRole(AppRoles.Admin))
            return NotFound(new { message = "Плейлист не знайдено." }); // Скрываем под 404, а не 403, чтобы не выдавать существование

        var result = await _playlistService.GetPlaylistByIdAsync(id, GetBaseUrl(), cancellationToken);

        if (!result.Success)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

    // PATCH music/playlists/{id}/privacy
    [HttpPatch("{id:guid}/privacy")]
    public async Task<IActionResult> UpdatePrivacy(
        Guid id,
        [FromBody] UpdatePlaylistPrivacyDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var playlist = await _playlistService.GetRawPlaylistAsync(id, cancellationToken);
        if (playlist == null)
            return NotFound(new { message = "Плейлист не знайдено." });

        // ПРОВЕРКА ДОСТУПА В КОНТРОЛЛЕРЕ:
        if (playlist.UserId != requesterId && HttpContext.UserIsInRole(AppRoles.Admin) == false)
            return Forbid();

        var result = await _playlistService.UpdatePrivacyAsync(id, dto.IsPrivate, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = dto.IsPrivate ? "Плейлист тепер приватний." : "Плейлист тепер публічний." });
    }

    // POST music/playlists/{id}/tracks
    [HttpPost("{id:guid}/tracks")]
    public async Task<IActionResult> AddTrack(
        Guid id,
        [FromBody] AddTrackToPlaylistDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var playlist = await _playlistService.GetRawPlaylistAsync(id, cancellationToken);
        if (playlist == null)
            return NotFound(new { message = "Плейлист не знайдено." });

        // ПРОВЕРКА ДОСТУПА В КОНТРОЛЛЕРЕ: (добавлять может только владелец)
        if (playlist.UserId != requesterId)
            return Forbid();

        var result = await _playlistService.AddTrackToPlaylistAsync(id, dto.TrackId, cancellationToken);

        return result switch
        {
            AddTrackResult.Added            => Ok(new { message = "Трек додано до плейлиста." }),
            AddTrackResult.AlreadyExists    => Conflict(new { message = "Цей трек вже є у плейлисті." }),
            AddTrackResult.TrackNotFound    => NotFound(new { message = "Трек не знайдено в базі." }),
            _                               => BadRequest()
        };
    }
    
    // DELETE music/playlists/{id}/tracks/{trackId}
    [HttpDelete("{id:guid}/tracks/{trackId:guid}")]
    public async Task<IActionResult> RemoveTrack(
        Guid id,
        Guid trackId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var playlist = await _playlistService.GetRawPlaylistAsync(id, cancellationToken);
        if (playlist == null)
            return NotFound(new { message = "Плейлист не знайдено." });

        // ПРОВЕРКА ДОСТУПА В КОНТРОЛЛЕРЕ:
        if (playlist.UserId != requesterId)
            return Forbid();

        var result = await _playlistService.RemoveTrackFromPlaylistAsync(id, trackId, cancellationToken);

        if (!result.Success)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(new { message = "Трек видалено з плейлиста." });
    }
    
    // GET music/playlists/deleted
    [HttpGet("deleted")]
    public async Task<IActionResult> GetDeletedPlaylists(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var result = await _playlistService.GetDeletedPlaylistsAsync(currentUserId, GetBaseUrl(), cancellationToken);
        return Ok(result.Data);
    }

// POST music/playlists/{id}/restore
    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> RestorePlaylist(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();

        var result = await _playlistService.RestorePlaylistAsync(id, currentUserId, userRole, cancellationToken);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Плейлист успішно відновлено." });
    }

    
    
    // PUT music/playlists/{id}/tracks/reorder
    [HttpPut("{id:guid}/tracks/reorder")]
    public async Task<IActionResult> ReorderTracks(
        Guid id,
        [FromBody] ReorderTracksDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var playlist = await _playlistService.GetRawPlaylistAsync(id, cancellationToken);
        if (playlist == null)
            return NotFound(new { message = "Плейлист не знайдено." });

        // ПРОВЕРКА ДОСТУПА В КОНТРОЛЛЕРЕ:
        if (playlist.UserId != requesterId && HttpContext.UserIsInRole(AppRoles.Admin) == false)
            return Forbid();

        var result = await _playlistService.ReorderTracksAsync(id, dto.OrderedTrackIds, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Порядок треків оновлено." });
    }

    // DELETE music/playlists/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeletePlaylist(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var playlist = await _playlistService.GetRawPlaylistAsync(id, cancellationToken);
        if (playlist == null)
            return NotFound(new { message = "Плейлист не знайдено." });

        // ПРОВЕРКА ДОСТУПА В КОНТРОЛЛЕРЕ:
        if (playlist.UserId != requesterId && HttpContext.UserIsInRole(AppRoles.Admin) == false)
            return Forbid();

        var result = await _playlistService.DeletePlaylistAsync(id, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Плейлист видалено." });
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private bool TryGetUserId(out Guid userId) =>
        HttpContext.TryGetUserId(out userId);
    private string GetBaseUrl() => $"{Request.Scheme}://{Request.Host}";

}