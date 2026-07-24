using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/favorites")]
public class FavoritesController : ControllerBase
{
    private readonly FavoritesService _favoritesService;

    public FavoritesController(FavoritesService favoritesService)
    {
        _favoritesService = favoritesService;
    }

    // ─── Tracks ─────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> LikeTrack([FromBody] LikeRequestDto dto)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var result = await _favoritesService.AddToFavoritesAsync(userId, dto.TrackId);

        if (!result)
            return BadRequest(new { message = "Не удалось добавить в избранное. Возможно, трека нет или он уже лайкнут." });

        return Ok(new { message = "Добавлено в избранное" });
    }

    [HttpDelete("{trackId:guid}")]
    public async Task<IActionResult> UnlikeTrack(Guid trackId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var result = await _favoritesService.RemoveFromFavoritesAsync(userId, trackId);

        if (!result)
            return NotFound(new { message = "Трек не найден в избранном" });

        return Ok(new { message = "Удалено из избранного" });
    }

    // Без pageNumber/pageSize — повний список (потрібен, наприклад, плеєру для обчислення
    // повного набору лайкнутих id). З pageNumber/pageSize — сторінка результатів для UI
    // зі стрічкою улюблених треків, що підвантажується частинами.
    [HttpGet]
    public async Task<IActionResult> GetMyFavorites(
        [FromQuery] int? pageNumber,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        if (pageNumber is null && pageSize is null)
        {
            var tracks = await _favoritesService.GetUserFavoriteTracksAsync(userId, baseUrl);
            return Ok(tracks);
        }

        var page = pageNumber ?? 1;
        var size = pageSize ?? 10;
        if (page < 1) page = 1;
        if (size < 1) size = 10;
        if (size > 100) size = 100;

        var (items, totalCount) = await _favoritesService.GetUserFavoriteTracksPagedAsync(userId, baseUrl, page, size, cancellationToken);
        return Ok(new PagedResultDto<TrackDto>(items, totalCount, page, size));
    }

    // ─── Albums ─────────────────────────────────────────────────────────────

    [HttpPost("albums")]
    public async Task<IActionResult> LikeAlbum([FromBody] LikeAlbumRequestDto dto)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var result = await _favoritesService.AddAlbumToFavoritesAsync(userId, dto.AlbumId);

        if (!result)
            return BadRequest(new { message = "Не удалось добавить альбом в избранное. Возможно, альбома нет или он уже лайкнут." });

        return Ok(new { message = "Альбом добавлен в избранное" });
    }

    [HttpDelete("albums/{albumId:guid}")]
    public async Task<IActionResult> UnlikeAlbum(Guid albumId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var result = await _favoritesService.RemoveAlbumFromFavoritesAsync(userId, albumId);

        if (!result)
            return NotFound(new { message = "Альбом не найден в избранном" });

        return Ok(new { message = "Альбом удалён из избранного" });
    }

    [HttpGet("albums")]
    public async Task<IActionResult> GetMyFavoriteAlbums()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var albums = await _favoritesService.GetUserFavoriteAlbumsAsync(userId, baseUrl);
        return Ok(albums);
    }
    
    
    // ─── Playlists ──────────────────────────────────────────────────────────

    [HttpPost("playlists")]
    public async Task<IActionResult> LikePlaylist([FromBody] LikePlaylistRequestDto dto)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var result = await _favoritesService.AddPlaylistToFavoritesAsync(userId, dto.PlaylistId);

        if (!result)
            return BadRequest(new { message = "Не вдалося додати плейлист в обране. Можливо, його немає, він приватний або вже лайкнутий." });

        return Ok(new { message = "Плейлист додано в обране" });
    }

    [HttpDelete("playlists/{playlistId:guid}")]
    public async Task<IActionResult> UnlikePlaylist(Guid playlistId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var result = await _favoritesService.RemovePlaylistFromFavoritesAsync(userId, playlistId);

        if (!result)
            return NotFound(new { message = "Плейлист не знайдено в обраному" });

        return Ok(new { message = "Плейлист видалено з обраного" });
    }

    [HttpGet("playlists")]
    public async Task<IActionResult> GetMyFavoritePlaylists(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var playlists = await _favoritesService.GetUserFavoritePlaylistsAsync(userId, baseUrl, cancellationToken);
        return Ok(playlists);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private bool TryGetUserId(out Guid userId) =>
        HttpContext.TryGetUserId(out userId);
}