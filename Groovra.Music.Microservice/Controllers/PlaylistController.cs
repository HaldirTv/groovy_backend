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

    public PlaylistsController(
        PlaylistService playlistService, FavoritesService favoritesService,
        GeminiAiService geminiAiService, ICacheService cache)
    {
        _playlistService = playlistService;
        _favoritesService = favoritesService;
        _geminiAiService = geminiAiService;
        _cache = cache;
    }

    // POST music/playlists/ai-mix — сгенерувати підбірку треків за текстовим запитом
    [HttpPost("ai-mix")]
    public async Task<IActionResult> GenerateAiMix([FromBody] GenerateAiMixRequestDto dto, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        if (string.IsNullOrWhiteSpace(dto.Prompt))
            return BadRequest(new { message = "Промпт не може бути порожнім." });

        var result = await _geminiAiService.GenerateAiMixAsync(userId, dto.Prompt, GetBaseUrl(), cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

    // GET music/playlists/ai-mixes — публічна стрічка вже згенерованих ШІ-міксів
    [HttpGet("ai-mixes")]
    public async Task<IActionResult> GetAiMixes(CancellationToken cancellationToken)
    {
        var result = await _playlistService.GetAiPlaylistsAsync(GetBaseUrl(), cancellationToken);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.Data);
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
    // Без pageNumber/pageSize — повний список (потрібен, наприклад, модалці "Додати до
    // плейлиста", де юзер обирає з усіх своїх плейлистів одразу). З pageNumber/pageSize —
    // сторінка результатів для UI зі стрічкою плейлистів, що підвантажується частинами.
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

        // ПРОВЕРКА ДОСТУПА В КОНТРОЛЛЕРЕ:
        // Если юзер смотрит свои плейлисты или он Админ - показываем всё (включая приватные).
        // Если смотрит чужие - только публичные.
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

        // Стрічку улюблених домішуємо за замовчуванням лише коли юзер дивиться власну
        // бібліотеку (на чужому профілі — ніколи). Явний query-параметр дозволяє фронтенду
        // попросити тільки власні плейлисти навіть на своїй бібліотеці — наприклад, сторінка
        // "Мої плейлисти" (сайдбар) навмисно показує лише створені юзером, тоді як стрічка
        // Library -> Playlists змішує власні й збережені.
        bool shouldIncludeFavorites = includeFavorites ?? (target == requesterId);

        var (items, totalCount) = await _playlistService.GetUserPlaylistsPagedAsync(target, includePrivate, shouldIncludeFavorites, page, size, cancellationToken);
        foreach (var p in items)
            p.IsLiked = likedIds.Contains(p.Id);

        return Ok(new PagedResultDto<PlaylistListItemDto>(items, totalCount, page, size));
    }

    // GET music/playlists/search — public playlists across all users, for extended search.
    // Distinct from GET music/playlists, which is scoped to one user's own playlists.
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
            return NotFound(new { message = "Плейлист не знайдено." }); // Скрываем под 404, а не 403, чтобы не выдавать існування

        // Проверяем, лайкнут ли плейлист текущим юзером
        var isLiked = requesterId != Guid.Empty &&
            (await _favoritesService.GetLikedPlaylistIdsAsync(requesterId, cancellationToken)).Contains(id);

        var result = await _playlistService.GetPlaylistByIdAsync(id, GetBaseUrl(), isLiked, cancellationToken);

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

    // DELETE music/playlists/{id}/permanent — остаточне видалення вже soft-deleted
    // плейлиста (з кошика), не чекаючи 30-денної фонової очистки.
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

    /// <summary>Cache-aside для публічного пошуку плейлистів. SearchPublicPlaylistsAsync
    /// не персоналізує IsLiked/IsOwner (немає userId-параметра), тому кешувати результат
    /// цілком безпечно — IsLiked поточного глядача накладається окремо після читання.</summary>
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