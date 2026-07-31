using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;
using Groovra.Shared.Extensions;
using Groovra.Shared.Constants;
using Groovra.Messaging.Contracts;
using MassTransit;

namespace Groovra.Music.Microservice.Services;

public class MusicService
{
    private readonly MusicDbContext _db;
    private readonly ILogger<MusicService> _logger;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ICacheService _cache;
    private readonly string _mediaBasePath;

    public MusicService(
        MusicDbContext db,
        IConfiguration configuration,
        ILogger<MusicService> logger,
        IPublishEndpoint publishEndpoint,
        ICacheService cache)
    {
        _db = db;
        _logger = logger;
        _publishEndpoint = publishEndpoint;
        _cache = cache;

        // AppContext.BaseDirectory, а НЕ Directory.GetCurrentDirectory(): текущий каталог —
        // это каталог, из которого процесс запустили, поэтому один и тот же exe искал медиа
        // то в bin, то в корне решения, и стрим локальных треков отдавал 404. BaseDirectory
        // всегда указывает на папку самого приложения, независимо от способа запуска.
        var configured = configuration["MediaStorage:BasePath"];
        _mediaBasePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "MediaStorage")
            : Path.GetFullPath(configured);
    }

    public async Task<(IReadOnlyList<Track> Items, int TotalCount)> GetAllTracksAsync(
        string? searchTerm = null,
        Guid? userId = null,
        string? genre = null,
        string? artist = null,
        int pageNumber = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Tracks.AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(t =>
                t.Title.Contains(searchTerm) ||
                t.ArtistName.Contains(searchTerm));
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            query = query.Where(t => t.ArtistName.Contains(artist));
        }

        if (!string.IsNullOrWhiteSpace(genre))
        {
            var trimmedGenre = genre.Trim();
            query = query.Where(t => t.Genre != null && t.Genre == trimmedGenre);
        }

        if (userId.HasValue)
        {
            query = query.Where(t => t.UserId == userId.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<string>> GetDistinctGenresAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Tracks
            .Where(t => t.Genre != null && t.Genre != "")
            .Select(t => t.Genre!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Track> Items, int TotalCount)> GetPopularTracksAsync(
        int pageNumber = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Tracks.AsQueryable();

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<Track?> GetTrackByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Tracks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task<(string AbsolutePath, string ContentType)?> GetTrackFileInfoAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var track = await _db.Tracks
            .AsNoTracking()
            .Select(t => new { t.Id, t.AudioRelativePath, t.ContentType })
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (track is null)
            return null;

        var absolutePath = Path.Combine(_mediaBasePath, track.AudioRelativePath);
        return (absolutePath, track.ContentType);
    }

    public async Task<IReadOnlyList<Track>> GetDeletedTracksAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.Tracks
            .IgnoreQueryFilters()
            .Where(t => t.IsDeleted && t.UserId == userId)
            .OrderByDescending(t => t.DeletedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RestoreTrackAsync(Guid trackId, Guid currentUserId, string userRoles, CancellationToken cancellationToken = default)
    {
        var track = await _db.Tracks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == trackId && t.IsDeleted, cancellationToken);

        if (track is null) return false;

        if (track.UserId != currentUserId && !userRoles.HasRole(AppRoles.Admin))
        {
            throw new UnauthorizedAccessException("You do not have permission to restore this track.");
        }

        track.IsDeleted = false;
        track.DeletedAt = null;

        if (track.AlbumId.HasValue)
        {
            var album = await _db.Albums
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.Id == track.AlbumId.Value && !a.IsDeleted, cancellationToken);

            if (album != null)
            {
                album.TrackCount++;
                album.TotalDurationSeconds += track.DurationSeconds;
                album.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        await InvalidateTrackCachesAsync(trackId, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteTrackAsync(Guid trackId,
    Guid currentUserId,
    string userRoles,
    CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var track = await _db.Tracks.FirstOrDefaultAsync(t => t.Id == trackId, cancellationToken);

        if (track is null)
        {
            _logger.LogWarning("Delete: трек {TrackId} не найден.", trackId);
            return false;
        }

        if (track.UserId != currentUserId && userRoles.HasRole(AppRoles.Admin) == false)
        {
            _logger.LogWarning("Security violation: Юзер {UserId} с ролью {Role} пытался удалить чужой трек {TrackId}",
                currentUserId, userRoles, trackId);
            throw new UnauthorizedAccessException("You do not have permission to delete this track.");
        }

        if (track.AlbumId.HasValue)
        {
            await _db.Albums
                .Where(a => a.Id == track.AlbumId.Value && !a.IsDeleted)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(a => a.TrackCount, a => a.TrackCount - 1)
                        .SetProperty(a => a.TotalDurationSeconds, a => a.TotalDurationSeconds - track.DurationSeconds)
                        .SetProperty(a => a.UpdatedAt, a => DateTime.UtcNow),
                    cancellationToken);
        }

        var playlistEntries = await _db.PlaylistTracks
            .Where(pt => pt.TrackId == trackId)
            .ToListAsync(cancellationToken);

        if (playlistEntries.Any())
        {
            var affectedPlaylistIds = playlistEntries.Select(pt => pt.PlaylistId).Distinct().ToList();

            _db.PlaylistTracks.RemoveRange(playlistEntries);

            foreach (var playlistId in affectedPlaylistIds)
            {
                await _db.Playlists
                    .Where(p => p.Id == playlistId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(p => p.TrackCount, p => p.TrackCount - 1)
                              .SetProperty(p => p.TotalDurationSeconds, p => p.TotalDurationSeconds - (int)Math.Round(track.DurationSeconds))
                              .SetProperty(p => p.UpdatedAt, p => DateTime.UtcNow),
                        cancellationToken);
            }
        }

        track.IsDeleted = true;
        track.DeletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await _publishEndpoint.Publish(new TrackDeletedEvent(trackId), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось опубликовать TrackDeletedEvent для трека {TrackId}. Откат транзакции.", trackId);
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        await transaction.CommitAsync(cancellationToken);
        await InvalidateTrackCachesAsync(trackId, cancellationToken);

        _logger.LogInformation("Трек успешно переведен в статус Soft-Deleted. Id={TrackId}, Title={Title}", trackId, track.Title);
        return true;
    }

    public async Task<bool> PermanentlyDeleteTrackAsync(
        Guid trackId, Guid currentUserId, string userRoles, CancellationToken cancellationToken = default)
    {
        var track = await _db.Tracks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == trackId && t.IsDeleted, cancellationToken);

        if (track is null) return false;

        if (track.UserId != currentUserId && !userRoles.HasRole(AppRoles.Admin))
            throw new UnauthorizedAccessException("You do not have permission to permanently delete this track.");

        var linkedPlaylistTracks = await _db.PlaylistTracks
            .Where(pt => pt.TrackId == trackId)
            .ToListAsync(cancellationToken);
        if (linkedPlaylistTracks.Any())
            _db.PlaylistTracks.RemoveRange(linkedPlaylistTracks);

        await DeleteOrphanedCommentsAsync(trackId, cancellationToken);

        await _db.Downloads
            .Where(d => d.Type == DownloadType.Track && d.ItemId == trackId)
            .ExecuteDeleteAsync(cancellationToken);

        if (!track.IsExternal)
        {
            DeleteRelativeFileIfExists(track.AudioRelativePath);
            DeleteRelativeFileIfExists(track.CoverImageRelativePath);
        }

        _db.Tracks.Remove(track);
        await _db.SaveChangesAsync(cancellationToken);
        await InvalidateTrackCachesAsync(trackId, cancellationToken);

        _logger.LogInformation("Трек остаточно видалено (БД + диск). Id={TrackId}, Title={Title}", trackId, track.Title);
        return true;
    }

    // TrackComment/TrackCommentLike have no FK relationship to Track (TrackId is a loose string
    // column), so hard-deleting a track leaves comments and comment-likes permanently orphaned
    // unless they're explicitly purged here.
    private async Task DeleteOrphanedCommentsAsync(Guid trackId, CancellationToken cancellationToken)
    {
        var trackIdString = trackId.ToString();

        var commentIds = await _db.TrackComments
            .IgnoreQueryFilters()
            .Where(c => c.TrackId == trackIdString)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (commentIds.Count == 0) return;

        await _db.TrackCommentLikes
            .Where(cl => commentIds.Contains(cl.CommentId))
            .ExecuteDeleteAsync(cancellationToken);

        await _db.TrackComments
            .IgnoreQueryFilters()
            .Where(c => c.TrackId == trackIdString)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private void DeleteRelativeFileIfExists(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        try
        {
            var absolutePath = Path.Combine(_mediaBasePath, relativePath.TrimStart('\\', '/'));
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при остаточному видаленні файлу: {RelativePath}", relativePath);
        }
    }

    public async Task<Track?> RenameTrackAsync(
        Guid id,
        string newTitle,
        Guid currentUserId,
        string userRoles,
        CancellationToken cancellationToken = default)
    {
        var track = await _db.Tracks.FindAsync([id], cancellationToken);

        if (track is null)
        {
            _logger.LogWarning("Rename: трек {TrackId} не найден.", id);
            return null;
        }
        if (string.IsNullOrWhiteSpace(newTitle))
            throw new ArgumentException("New title cannot be empty.", nameof(newTitle));
        if(track.UserId != currentUserId && userRoles.HasRole(AppRoles.Admin) == false)
            throw new UnauthorizedAccessException("You do not have permission to rename this track.");

        var oldTitle = track.Title;
        track.Title = newTitle.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        await InvalidateTrackCachesAsync(id, cancellationToken);

        _logger.LogInformation(
            "Трек переименован. Id={TrackId}, '{OldTitle}' → '{NewTitle}'",
            id, oldTitle, track.Title);

        return track;
    }

    public async Task<bool> IncrementPlayCountAsync(Guid userId, Guid trackId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var updated = await _db.Tracks
            .Where(t => t.Id == trackId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.PlayCount, t => t.PlayCount + 1),
                cancellationToken);

        if (updated == 0)
        {
            _logger.LogWarning("IncrementPlayCount: трек {TrackId} не найден.", trackId);
            return false;
        }

        try
        {

            await _publishEndpoint.Publish(new TrackPlayedEvent(
                UserId: userId,
                TrackId: trackId,
                PlayedAt: DateTime.UtcNow
            ), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось опубликовать TrackPlayedEvent для трека {TrackId}. Откат транзакции.", trackId);
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.LogDebug("PlayCount увеличен для трека {TrackId}.", trackId);
        return true;
    }

    private async Task InvalidateTrackCachesAsync(Guid trackId, CancellationToken cancellationToken)
    {
        await _cache.RemoveAsync(CacheKeys.Track(trackId), cancellationToken);
        foreach (var pattern in CacheKeys.ListPatterns)
        {
            await _cache.RemoveByPatternAsync(pattern, cancellationToken);
        }
    }

    private void DeleteFileIfExists(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            _logger.LogWarning("Файл не найден при удалении: {Path}", absolutePath);
            return;
        }

        File.Delete(absolutePath);
        _logger.LogInformation("Файл удалён: {Path}", absolutePath);
    }

    public async Task<IReadOnlyList<(string Mood, IReadOnlyList<Track> Tracks)>> GetMoodRecommendationsAsync(
        int perMoodCount = 8,
        CancellationToken cancellationToken = default)
    {
        var moodCount = MoodCatalog.FallbackGenres.Count;

        var taggedByMood = (await _db.Tracks
                .Where(t => t.Mood != null)
                .OrderByDescending(t => t.PlayCount)
                .ThenBy(t => t.Id)
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.Mood!.Trim().ToLower())
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Track>)g.ToList());

        var allFallbackGenres = MoodCatalog.FallbackGenres.Values
            .SelectMany(g => g)
            .Distinct()
            .ToArray();
        var genreCandidates = await _db.Tracks
            .Where(t => t.Genre != null && allFallbackGenres.Contains(t.Genre))
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var globalPopular = await _db.Tracks
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id)
            .Take(perMoodCount * moodCount)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var result = new List<(string, IReadOnlyList<Track>)>();

        foreach (var (mood, fallbackGenres) in MoodCatalog.FallbackGenres)
        {
            var picked = new List<Track>();
            var pickedIds = new HashSet<Guid>();

            if (taggedByMood.TryGetValue(mood.Trim().ToLower(), out var byMood))
            {
                picked.AddRange(byMood.Take(perMoodCount));
                pickedIds.UnionWith(picked.Select(t => t.Id));
            }

            if (picked.Count < perMoodCount)
            {
                var moodGenres = fallbackGenres.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var byGenre = genreCandidates
                    .Where(t => !pickedIds.Contains(t.Id) && t.Genre != null && moodGenres.Contains(t.Genre))
                    .Take(perMoodCount - picked.Count)
                    .ToList();
                picked.AddRange(byGenre);
                pickedIds.UnionWith(byGenre.Select(t => t.Id));
            }

            if (picked.Count < perMoodCount)
            {
                var filler = globalPopular
                    .Where(t => !pickedIds.Contains(t.Id))
                    .Take(perMoodCount - picked.Count);
                picked.AddRange(filler);
            }

            if (picked.Count > 0)
                result.Add((mood, picked));
        }

        return result;
    }

    public async Task<List<Track>> GetPersonalizedRecommendationsAsync(
        IReadOnlyCollection<Guid> signalTrackIds,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (signalTrackIds.Count == 0) return new List<Track>();

        var signalTracks = await _db.Tracks
            .Where(t => signalTrackIds.Contains(t.Id))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var topGenres = signalTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Genre))
            .GroupBy(t => t.Genre!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(2)
            .Select(g => g.Key)
            .ToList();

        var topMoods = signalTracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Mood))
            .GroupBy(t => t.Mood!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(2)
            .Select(g => g.Key)
            .ToList();

        if (topGenres.Count == 0 && topMoods.Count == 0)
            return new List<Track>();

        return await _db.Tracks
            .Where(t => (t.Genre != null && topGenres.Contains(t.Genre))
                || (t.Mood != null && topMoods.Contains(t.Mood)))
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}
