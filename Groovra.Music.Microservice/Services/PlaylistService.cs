using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Shared.ServiceResult;
using Groovra.Music.Microservice.Result;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;

namespace Groovra.Music.Microservice.Services;

public class PlaylistService
{
    private readonly MusicDbContext _context;
    private readonly ILogger<PlaylistService> _logger;
    private readonly ICacheService _cache;

    public PlaylistService(MusicDbContext context, ICacheService cache, ILogger<PlaylistService> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Скидає кеш публічного пошуку плейлистів — викликається з кожного методу,
    /// що змінює каталог/видимість/склад плейлиста.</summary>
    private Task InvalidatePlaylistsCacheAsync(CancellationToken cancellationToken) =>
        _cache.RemoveByPatternAsync(CacheKeys.PlaylistsSearchPatternAll, cancellationToken);

    public async Task<Playlist?> GetRawPlaylistAsync(Guid playlistId, CancellationToken cancellationToken = default)
    {
        return await _context.Playlists
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);
    }

    // ─── Create ──────────────────────────────────────────────────────────────

    public async Task<ServiceResult<PlaylistDto>> CreatePlaylistAsync(
        Guid userId,
        string title,
        string? description,
        string baseUrl,
        bool isPrivate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return ServiceResult<PlaylistDto>.Fail("Назва плейлиста не може бути порожньою.");

        var baseSlug = GenerateSlug(title);
        var uniqueSlug = baseSlug;
        int counter = 1;
        
        while (await _context.Playlists.IgnoreQueryFilters()
                   .AnyAsync(p => p.Slug == uniqueSlug, cancellationToken))
        {
            uniqueSlug = $"{baseSlug}-{counter++}";
        }

        var playlist = new Playlist
        {
            Id          = Guid.NewGuid(),
            UserId      = userId,
            Title       = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            IsPrivate   = isPrivate,
            Slug        = uniqueSlug,
            TrackCount  = 0,
            TotalDurationSeconds = 0,
            IsDeleted   = false,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow,
        };

        _context.Playlists.Add(playlist);
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePlaylistsCacheAsync(cancellationToken);

        _logger.LogInformation("Playlist created. Id={Id}, Slug={Slug}", playlist.Id, playlist.Slug);
        return ServiceResult<PlaylistDto>.Ok(MapToDto(playlist, baseUrl, isLiked: false)); 
    }

    // ─── GetUserPlaylists ─────────────────────────────────────────────────────

    public async Task<ServiceResult<IReadOnlyList<PlaylistListItemDto>>> GetUserPlaylistsAsync(
        Guid targetUserId,
        bool includePrivate,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Playlists
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .Where(p => p.UserId == targetUserId);

        if (!includePrivate)
            query = query.Where(p => !p.IsPrivate);

        var result = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new PlaylistListItemDto
            {
                Id                   = p.Id,
                Title                = p.Title,
                Description          = p.Description,
                IsPrivate            = p.IsPrivate,
                IsOwner              = p.UserId == targetUserId,
                Slug                 = p.Slug ?? string.Empty,
                TrackCount           = p.TrackCount,
                TotalDurationSeconds = p.TotalDurationSeconds,
                CoverImageUrl        = p.CoverImageUrl,
                UpdatedAt            = p.UpdatedAt,
                CollageCovers = p.Tracks
                    .OrderBy(pt => pt.Position)
                    .Take(4)
                    .Select(pt => pt.Track!.IsExternal
                        ? pt.Track.ExternalCoverUrl
                        : pt.Track.CoverImageRelativePath)
                    .Where(url => url != null)
                    .Select(url => url!)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return ServiceResult<IReadOnlyList<PlaylistListItemDto>>.Ok(result);
    }

    /// <summary>
    /// Пагінований варіант <see cref="GetUserPlaylistsAsync"/> — для UI зі стрічкою плейлистів,
    /// що підвантажується частинами (кнопка "Завантажити ще"). Коли <paramref name="includeFavorites"/>
    /// увімкнено, до власних плейлистів <paramref name="targetUserId"/> домішуються й ті чужі публічні
    /// плейлисти, які він лайкнув (тобто стрічка = "мої" + "збережені"), позначені прапорцем IsOwner.
    /// </summary>
    public async Task<(IReadOnlyList<PlaylistListItemDto> Items, int TotalCount)> GetUserPlaylistsPagedAsync(
        Guid targetUserId,
        bool includePrivate,
        bool includeFavorites,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Playlists
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .Where(p =>
                (p.UserId == targetUserId && (includePrivate || !p.IsPrivate))
                || (includeFavorites && !p.IsPrivate &&
                    _context.FavoritePlaylists.Any(f => f.UserId == targetUserId && f.PlaylistId == p.Id)));

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(p => p.UpdatedAt)
            .ThenBy(p => p.Id) // стабільний тайбрейкер на випадок збігу UpdatedAt
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PlaylistListItemDto
            {
                Id                   = p.Id,
                Title                = p.Title,
                Description          = p.Description,
                IsPrivate            = p.IsPrivate,
                IsOwner              = p.UserId == targetUserId,
                Slug                 = p.Slug ?? string.Empty,
                TrackCount           = p.TrackCount,
                TotalDurationSeconds = p.TotalDurationSeconds,
                CoverImageUrl        = p.CoverImageUrl,
                UpdatedAt            = p.UpdatedAt,
                CollageCovers = p.Tracks
                    .OrderBy(pt => pt.Position)
                    .Take(4)
                    .Select(pt => pt.Track!.IsExternal
                        ? pt.Track.ExternalCoverUrl
                        : pt.Track.CoverImageRelativePath)
                    .Where(url => url != null)
                    .Select(url => url!)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    /// <summary>
    /// Searches public (non-private, non-deleted) playlists across all users by title — backs the
    /// "Playlists" category in extended search, as opposed to GetUserPlaylistsAsync which is
    /// scoped to one user's own playlists.
    /// </summary>
    public async Task<(IReadOnlyList<PlaylistListItemDto> Items, int TotalCount)> SearchPublicPlaylistsAsync(
        string? searchTerm,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Playlists
            .AsNoTracking()
            .Where(p => !p.IsDeleted && !p.IsPrivate);

        if (!string.IsNullOrWhiteSpace(searchTerm))
            query = query.Where(p => p.Title.Contains(searchTerm));

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(p => p.UpdatedAt)
            .ThenBy(p => p.Id) // стабільний тайбрейкер на випадок збігу UpdatedAt
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PlaylistListItemDto
            {
                Id                   = p.Id,
                Title                = p.Title,
                Description          = p.Description,
                IsPrivate            = p.IsPrivate,
                Slug                 = p.Slug ?? string.Empty,
                TrackCount           = p.TrackCount,
                TotalDurationSeconds = p.TotalDurationSeconds,
                CoverImageUrl        = p.CoverImageUrl,
                UpdatedAt            = p.UpdatedAt,
                CollageCovers = p.Tracks
                    .OrderBy(pt => pt.Position)
                    .Take(4)
                    .Select(pt => pt.Track!.IsExternal
                        ? pt.Track.ExternalCoverUrl
                        : pt.Track.CoverImageRelativePath)
                    .Where(url => url != null)
                    .Select(url => url!)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    // ─── GetById ──────────────────────────────────────────────────────────────

    public async Task<ServiceResult<PlaylistDto>> GetPlaylistByIdAsync(
        Guid playlistId,
        string baseUrl,
        bool isLiked = false,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists
            .Where(p => !p.IsDeleted)
            .Include(p => p.Tracks.OrderBy(pt => pt.Position))
                .ThenInclude(pt => pt.Track)
            .FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);

        if (playlist is null)
            return ServiceResult<PlaylistDto>.Fail("Плейлист не знайдено.");

        return ServiceResult<PlaylistDto>.Ok(MapToDto(playlist, baseUrl, isLiked));
    }

    // ─── UpdatePrivacy ────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> UpdatePrivacyAsync(
        Guid playlistId,
        bool isPrivate,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId && !p.IsDeleted, cancellationToken);
        if (playlist is null) return ServiceResult<bool>.Fail("Плейлист не знайдено.");

        playlist.IsPrivate = isPrivate;
        playlist.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePlaylistsCacheAsync(cancellationToken);

        return ServiceResult<bool>.Ok(true);
    }

    // ─── AddTrack ─────────────────────────────────────────────────────────────

    public async Task<AddTrackResult> AddTrackToPlaylistAsync(
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId && !p.IsDeleted, cancellationToken);
        if (playlist is null) return AddTrackResult.PlaylistNotFound;

        var track = await _context.Tracks
            .AsNoTracking()
            .Select(t => new { t.Id, t.DurationSeconds })
            .FirstOrDefaultAsync(t => t.Id == trackId, cancellationToken);

        if (track is null) return AddTrackResult.TrackNotFound;

        var alreadyAdded = await _context.PlaylistTracks
            .AnyAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, cancellationToken);

        if (alreadyAdded) return AddTrackResult.AlreadyExists;

        var nextPosition = await _context.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .MaxAsync(pt => (int?)pt.Position, cancellationToken) + 1 ?? 1;

        _context.PlaylistTracks.Add(new PlaylistTrack
        {
            PlaylistId = playlistId,
            TrackId    = trackId,
            Position   = nextPosition,
            AddedAt    = DateTime.UtcNow,
        });

        playlist.TrackCount++;
        playlist.TotalDurationSeconds += (int)Math.Round(track.DurationSeconds);
        playlist.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePlaylistsCacheAsync(cancellationToken);
        return AddTrackResult.Added;
    }

    // ─── RemoveTrack ──────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> RemoveTrackFromPlaylistAsync(
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId && !p.IsDeleted, cancellationToken);
        if (playlist is null) return ServiceResult<bool>.Fail("Плейлист не знайдено.");

        var entry = await _context.PlaylistTracks
            .FirstOrDefaultAsync(pt => pt.PlaylistId == playlistId && pt.TrackId == trackId, cancellationToken);

        if (entry is null) return ServiceResult<bool>.Fail("Трек не знайдено у плейлисті.");

        var trackDuration = await _context.Tracks
            .AsNoTracking()
            .Where(t => t.Id == trackId)
            .Select(t => t.DurationSeconds)
            .FirstOrDefaultAsync(cancellationToken);

        var removedPosition = entry.Position;
    
        // 1. Удаляем запись из контекста
        _context.PlaylistTracks.Remove(entry);
        // ФИКС: Физически удаляем трек из базы прямо сейчас, чтобы освободить его порядковый номер (Position)
        await _context.SaveChangesAsync(cancellationToken); 

        // 2. Теперь безопасно сдвигаем позиции оставшихся треков
        await _context.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.Position > removedPosition)
            .ExecuteUpdateAsync(
                s => s.SetProperty(pt => pt.Position, pt => pt.Position - 1),
                cancellationToken);

        playlist.TrackCount = Math.Max(0, playlist.TrackCount - 1);
        playlist.TotalDurationSeconds = Math.Max(0, playlist.TotalDurationSeconds - (int)Math.Round(trackDuration));
        playlist.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await InvalidatePlaylistsCacheAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── ReorderTracks ────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> ReorderTracksAsync(
        Guid playlistId,
        IList<Guid> orderedTrackIds,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId && !p.IsDeleted, cancellationToken);
        if (playlist is null) return ServiceResult<bool>.Fail("Плейлист не знайдено.");

        var entries = await _context.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .ToListAsync(cancellationToken);

        var existingIds = entries.Select(e => e.TrackId).ToHashSet();
        if (orderedTrackIds.Count != existingIds.Count || !orderedTrackIds.All(id => existingIds.Contains(id)))
            return ServiceResult<bool>.Fail("Список ID не відповідає вмісту плейлиста.");

        var positionMap = orderedTrackIds
            .Select((id, index) => (id, position: index + 1))
            .ToDictionary(x => x.id, x => x.position);

        foreach (var entry in entries)
            entry.Position = positionMap[entry.TrackId];

        playlist.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePlaylistsCacheAsync(cancellationToken);

        return ServiceResult<bool>.Ok(true);
    }

    // ─── SoftDelete ───────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> DeletePlaylistAsync(
        Guid playlistId,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId && !p.IsDeleted, cancellationToken);
        if (playlist is null) return ServiceResult<bool>.Fail("Плейлист не знайдено.");

        playlist.IsDeleted = true;
        playlist.DeletedAt = DateTime.UtcNow;
        playlist.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePlaylistsCacheAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }


    public async Task<ServiceResult<IReadOnlyList<PlaylistListItemDto>>> GetDeletedPlaylistsAsync(
        Guid userId, string baseUrl, CancellationToken cancellationToken = default)
    {
        var result = await _context.Playlists
            .IgnoreQueryFilters()
            .Where(p => p.IsDeleted && p.UserId == userId)
            .OrderByDescending(p => p.DeletedAt)
            .Select(p => new PlaylistListItemDto
            {
                Id                   = p.Id,
                Title                = p.Title,
                Description          = p.Description,
                IsPrivate            = p.IsPrivate,
                Slug                 = p.Slug,
                TrackCount           = p.TrackCount,
                TotalDurationSeconds = (double)p.TotalDurationSeconds,
                CoverImageUrl        = p.CoverImageUrl,
                UpdatedAt            = p.UpdatedAt,
                CollageCovers = p.Tracks
                    .OrderBy(pt => pt.Position)
                    .Take(4)
                    .Select(pt => pt.Track!.IsExternal ? pt.Track.ExternalCoverUrl : pt.Track.CoverImageRelativePath)
                    .Where(url => url != null)
                    .ToList()!,
            })
            .ToListAsync(cancellationToken);

        foreach (var item in result)
        {
            item.CollageCovers = item.CollageCovers
                .Select(url => url != null && !url.StartsWith("http")
                    ? $"{baseUrl}/music/files/{url.Replace('\\', '/')}"
                    : url)
                .ToList()!;
        }

        return ServiceResult<IReadOnlyList<PlaylistListItemDto>>.Ok(result);
    }


    public async Task<ServiceResult<bool>> RestorePlaylistAsync(
        Guid playlistId, Guid currentUserId, string userRoles, CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.IsDeleted, cancellationToken);

        if (playlist is null) 
            return ServiceResult<bool>.Fail("Видалений плейлист не знайдено.");

        if (playlist.UserId != currentUserId && !userRoles.HasRole(AppRoles.Admin))
            return ServiceResult<bool>.Fail("Немає прав для відновлення цього плейлиста.");

        playlist.IsDeleted = false;
        playlist.DeletedAt = null;
        playlist.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePlaylistsCacheAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── PermanentDelete ─────────────────────────────────────────────────────

    /// <summary>Остаточно видаляє вже soft-deleted плейлист (з кошика) — не чекаючи
    /// 30-денної фонової очистки (<see cref="GarbageCollectorService.CleanUpGarbageAsync"/>).
    /// Треки з плейлиста не чіпаємо — видаляються лише зв'язки PlaylistTracks.</summary>
    public async Task<ServiceResult<bool>> PermanentlyDeletePlaylistAsync(
        Guid playlistId, Guid currentUserId, string userRoles, CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == playlistId && p.IsDeleted, cancellationToken);

        if (playlist is null)
            return ServiceResult<bool>.Fail("Видалений плейлист не знайдено.");

        if (playlist.UserId != currentUserId && !userRoles.HasRole(AppRoles.Admin))
            return ServiceResult<bool>.Fail("Немає прав для остаточного видалення цього плейлиста.");

        var entries = await _context.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId)
            .ToListAsync(cancellationToken);
        if (entries.Any())
            _context.PlaylistTracks.RemoveRange(entries);

        _context.Playlists.Remove(playlist);
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidatePlaylistsCacheAsync(cancellationToken);

        _logger.LogInformation("Playlist permanently deleted. Id={Id}", playlistId);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static PlaylistDto MapToDto(Playlist p, string baseUrl = "", bool isLiked = false)
    {
        return new PlaylistDto(
            p.Id,
            p.UserId,
            p.Title,
            p.Description,
            p.Slug,
            p.CoverImageUrl,
            p.TrackCount,
            p.TotalDurationSeconds,
            p.IsPrivate,
            isLiked,
            p.CreatedAt,
            p.Tracks?.Select(pt => new PlaylistTrackDto(
                pt.TrackId,
                pt.Track?.Title ?? "Unknown",
                pt.Track?.ArtistName ?? "Unknown",
                pt.Position,
                pt.Track?.IsExternal == true
                    ? pt.Track.ExternalCoverUrl
                    : pt.Track?.CoverImageRelativePath != null
                        ? $"{baseUrl}/music/files/{pt.Track.CoverImageRelativePath.Replace('\\', '/')}"
                        : null,
                pt.Track?.DurationSeconds ?? 0
            )).ToList() ?? new List<PlaylistTrackDto>()
        );
    }

    /// <summary>
    /// Возвращает до 8 плейлистов, помеченных как AI-подборки (по слагу), для публичной ленты
    /// "ШІ Мікси" — используется GeminiAiService при создании и GET music/playlists/ai-mixes.
    /// </summary>
    public async Task<ServiceResult<IReadOnlyList<PlaylistListItemDto>>> GetAiPlaylistsAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var result = await _context.Playlists
            .AsNoTracking()
            .Where(p => !p.IsDeleted && !p.IsPrivate && p.Slug != null && p.Slug.Contains("ai-mix"))
            .OrderByDescending(p => p.CreatedAt)
            .Take(8)
            .Select(p => new PlaylistListItemDto
            {
                Id                   = p.Id,
                Title                = p.Title,
                Description          = p.Description,
                IsPrivate            = p.IsPrivate,
                Slug                 = p.Slug ?? string.Empty,
                TrackCount           = p.TrackCount,
                TotalDurationSeconds = p.TotalDurationSeconds,
                CoverImageUrl        = p.CoverImageUrl,
                UpdatedAt            = p.UpdatedAt,
                CollageCovers = p.Tracks
                    .OrderBy(pt => pt.Position)
                    .Take(4)
                    .Select(pt => pt.Track!.IsExternal
                        ? pt.Track.ExternalCoverUrl
                        : pt.Track.CoverImageRelativePath)
                    .Where(url => url != null)
                    .Select(url => url!)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        // Локальні (не-зовнішні) обкладинки з БД зберігаються як відносний шлях —
        // без цього постпроцесингу фронтенд отримає щось типу "covers/xyz.jpg" і спробує
        // завантажити картинку відносно свого власного origin, а не gateway (як і в
        // GetDeletedPlaylistsAsync нижче, той самий фікс).
        foreach (var item in result)
        {
            item.CollageCovers = item.CollageCovers
                .Select(url => !url.StartsWith("http") ? $"{baseUrl}/music/files/{url.Replace('\\', '/')}" : url)
                .ToList();
        }

        return ServiceResult<IReadOnlyList<PlaylistListItemDto>>.Ok(result);
    }

    internal static string GenerateSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "playlist";

        var str = title.ToLowerInvariant().Trim();
        str = Transliterate(str);
        str = Regex.Replace(str, @"[^a-z0-9\s-]", "");
        str = Regex.Replace(str, @"\s+", " ").Replace(" ", "-");
        str = Regex.Replace(str, @"-+", "-");
        return str.Trim('-');
    }

    private static string Transliterate(string text)
    {
        string[] cyr = { "а","б","в","г","д","е","є","ж","з","и","і","ї","й","к","л","м","н","о","п","р","с","т","у","ф","х","ц","ч","ш","щ","ь","ю","я" };
        string[] lat = { "a","b","v","g","d","e","ye","zh","z","y","i","yi","y","k","l","m","n","o","p","r","s","t","u","f","kh","ts","ch","sh","shch","","yu","ya" };
        for (int i = 0; i < cyr.Length; i++) text = text.Replace(cyr[i], lat[i]);
        return text.Replace("ё","yo").Replace("ъ","").Replace("ы","y").Replace("э","e");
    }
}