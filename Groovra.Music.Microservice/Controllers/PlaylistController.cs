using Groovra.Music.Microservice.Caching;
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
    private readonly FavoritesService _favoritesService;
    private readonly GeminiAiService _geminiAiService;
    private readonly ICacheService _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public PlaylistsController(
        PlaylistService playlistService, FavoritesService favoritesService, GeminiAiService geminiAiService,
        ICacheService cache, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _playlistService = playlistService;
        _favoritesService = favoritesService;
        _geminiAiService = geminiAiService;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    [HttpPost("ai-mix")]
    public async Task<IActionResult> GenerateAiMix(
        [FromBody] GenerateAiMixRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        if (string.IsNullOrWhiteSpace(dto.Prompt))
            return BadRequest(new { message = "Промпт не може бути порожнім." });

        try
        {
            var billingBaseUrl = _configuration["Billing:BaseUrl"] ?? "http://localhost:5041";
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
            var billingRes = await client.PostAsync($"{billingBaseUrl.TrimEnd('/')}/billing/ai-mix/check-and-increment", null, cancellationToken);
            if (!billingRes.IsSuccessStatusCode)
            {
                var jsonString = await billingRes.Content.ReadAsStringAsync(cancellationToken);
                return StatusCode((int)billingRes.StatusCode, jsonString);
            }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Billing check warning: {ex.Message}");
        }

        var result = await _geminiAiService.GenerateAiMixAsync(
            userId, dto.Prompt, GetBaseUrl(), cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpGet("ai-mixes")]
    public async Task<IActionResult> GetAiMixes(CancellationToken cancellationToken)
    {
        var result = await _playlistService.GetAiPlaylistsAsync(GetBaseUrl(), cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

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

    [HttpGet]
    public async Task<IActionResult> GetPlaylists(
        [FromQuery] Guid? targetUserId,
        [FromQuery] int? pageNumber,
        [FromQuery] int? pageSize,
        [FromQuery] bool? includeFavorites,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var target = targetUserId ?? requesterId;

        bool includePrivate = (target == requesterId) || HttpContext.UserIsInRole(AppRoles.Admin);

        var likedIds = await _favoritesService.GetLikedPlaylistIdsAsync(requesterId, cancellationToken);

        if (pageNumber is null && pageSize is null)
        {
            var result = await _playlistService.GetUserPlaylistsAsync(target, includePrivate, cancellationToken);

            if (!result.Success)
                return BadRequest(new { message = result.ErrorMessage });

            var playlists = result.Data.Select(p =>
            {
                p.IsLiked = likedIds.Contains(p.Id);
                return p;
            }).ToList();

            return Ok(playlists);
        }

        var page = pageNumber ?? 1;
        var size = pageSize ?? 10;
        if (page < 1) page = 1;
        if (size < 1) size = 10;
        if (size > 100) size = 100;

        bool shouldIncludeFavorites = includeFavorites ?? (target == requesterId);

        var (items, totalCount) = await _playlistService.GetUserPlaylistsPagedAsync(target, includePrivate, shouldIncludeFavorites, page, size, cancellationToken);
        foreach (var p in items)
            p.IsLiked = likedIds.Contains(p.Id);

        return Ok(new PagedResultDto<PlaylistListItemDto>(items, totalCount, page, size));
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchPlaylists(
        [FromQuery] string? search,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var (items, totalCount) = await GetPublicPlaylistsWithCacheAsync(search, pageNumber, pageSize, cancellationToken);

        HttpContext.TryGetUserId(out var requesterId);
        if (requesterId != Guid.Empty)
        {
            var likedIds = await _favoritesService.GetLikedPlaylistIdsAsync(requesterId, cancellationToken);
            foreach (var playlist in items)
                playlist.IsLiked = likedIds.Contains(playlist.Id);
        }

        return Ok(new PagedResultDto<PlaylistListItemDto>(items, totalCount, pageNumber, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetPlaylist(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var playlist = await _playlistService.GetRawPlaylistAsync(id, cancellationToken);
        if (playlist == null)
            return NotFound(new { message = "Плейлист не знайдено." });

        if (playlist.IsPrivate && playlist.UserId != requesterId && !HttpContext.UserIsInRole(AppRoles.Admin))
            return NotFound(new { message = "Плейлист не знайдено." }); 

        var isLiked = requesterId != Guid.Empty &&
            (await _favoritesService.GetLikedPlaylistIdsAsync(requesterId, cancellationToken)).Contains(id);

        var result = await _playlistService.GetPlaylistByIdAsync(id, GetBaseUrl(), isLiked, cancellationToken);

        if (!result.Success)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

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

        if (playlist.UserId != requesterId && HttpContext.UserIsInRole(AppRoles.Admin) == false)
            return Forbid();

        var result = await _playlistService.UpdatePrivacyAsync(id, dto.IsPrivate, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = dto.IsPrivate ? "Плейлист тепер приватний." : "Плейлист тепер публічний." });
    }

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

        if (playlist.UserId != requesterId)
            return Forbid();

        var result = await _playlistService.RemoveTrackFromPlaylistAsync(id, trackId, cancellationToken);

        if (!result.Success)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(new { message = "Трек видалено з плейлиста." });
    }
    [HttpGet("deleted")]
    public async Task<IActionResult> GetDeletedPlaylists(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var result = await _playlistService.GetDeletedPlaylistsAsync(currentUserId, GetBaseUrl(), cancellationToken);
        return Ok(result.Data);
    }

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

    // DELETE music/playlists/{id}/permanent — окончательное удаление уже soft-deleted
    // плейлиста (из корзины), не дожидаясь фоновой очистки.
    [HttpDelete("{id:guid}/permanent")]
    public async Task<IActionResult> PermanentlyDeletePlaylist(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();

        var result = await _playlistService.PermanentlyDeletePlaylistAsync(id, currentUserId, userRole, cancellationToken);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Плейлист остаточно видалено." });
    }

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

        if (playlist.UserId != requesterId && HttpContext.UserIsInRole(AppRoles.Admin) == false)
            return Forbid();

        var result = await _playlistService.ReorderTracksAsync(id, dto.OrderedTrackIds, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Порядок треків оновлено." });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeletePlaylist(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var requesterId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var playlist = await _playlistService.GetRawPlaylistAsync(id, cancellationToken);
        if (playlist == null)
            return NotFound(new { message = "Плейлист не знайдено." });

        if (playlist.UserId != requesterId && HttpContext.UserIsInRole(AppRoles.Admin) == false)
            return Forbid();

        var result = await _playlistService.DeletePlaylistAsync(id, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Плейлист видалено." });
    }

    private bool TryGetUserId(out Guid userId) =>
        HttpContext.TryGetUserId(out userId);
    private string GetBaseUrl() => $"{Request.Scheme}://{Request.Host}";

    /// <summary>Cache-aside для публичного поиска плейлистов. Результат не персонализирован
    /// (нет userId), поэтому кешируется целиком — IsLiked накладывается отдельно после чтения.</summary>
    private async Task<(IReadOnlyList<PlaylistListItemDto> Items, int TotalCount)> GetPublicPlaylistsWithCacheAsync(
        string? search, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        var key = CacheKeys.PlaylistsSearch(search, pageNumber, pageSize);
        var cached = await _cache.GetAsync<CachedPage<PlaylistListItemDto>>(key, cancellationToken);
        if (cached is not null)
            return (cached.Items, cached.TotalCount);

        var (items, totalCount) = await _playlistService.SearchPublicPlaylistsAsync(search, pageNumber, pageSize, cancellationToken);
        await _cache.SetAsync(
            key, new CachedPage<PlaylistListItemDto> { Items = items.ToList(), TotalCount = totalCount },
            TimeSpan.FromMinutes(5), cancellationToken);

        return (items, totalCount);
    }
}