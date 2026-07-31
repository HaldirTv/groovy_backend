using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

public class GarbageCollectorService
{
    private readonly MusicDbContext _db;
    private readonly ILogger<GarbageCollectorService> _logger;
    private readonly string _mediaStoragePath;

    public GarbageCollectorService(
        MusicDbContext db, 
        IConfiguration configuration, 
        ILogger<GarbageCollectorService> logger)
    {
        _db = db;
        _logger = logger;
        // AppContext.BaseDirectory, а не текущий каталог процесса (см. MusicService) — иначе
        // сборщик мусора чистил бы файлы не в той папке, где они реально лежат.
        var basePathConfig = configuration["MediaStorage:BasePath"];
        _mediaStoragePath = string.IsNullOrWhiteSpace(basePathConfig)
            ? Path.Combine(AppContext.BaseDirectory, "MediaStorage")
            : Path.GetFullPath(basePathConfig);
    }

    public async Task CleanUpGarbageAsync(CancellationToken cancellationToken = default)
    {
        var expiryDate = DateTime.UtcNow.AddDays(-30);

        _logger.LogInformation("Запуск планової очистки кошика Hangfire. Поріг дати: {ExpiryDate}", expiryDate);

        var oldTracks = await _db.Tracks
            .IgnoreQueryFilters()
            .Where(t => t.IsDeleted && t.DeletedAt < expiryDate)
            .ToListAsync(cancellationToken);

        if (oldTracks.Any())
        {
            var trackIds = oldTracks.Select(t => t.Id).ToList();

            var linkedPlaylistTracks = await _db.PlaylistTracks
                .Where(pt => trackIds.Contains(pt.TrackId))
                .ToListAsync(cancellationToken);
            if (linkedPlaylistTracks.Any())
            {
                _db.PlaylistTracks.RemoveRange(linkedPlaylistTracks);
                _logger.LogInformation("Видалено {Count} зв'язків треків із плейлистами.", linkedPlaylistTracks.Count);
            }

            // TrackComment/TrackCommentLike have no FK relationship to Track (TrackId is a loose
            // string column) so they must be purged explicitly - otherwise they're permanently
            // orphaned once the track row below is hard-deleted.
            var trackIdStrings = trackIds.Select(id => id.ToString()).ToList();
            var orphanedCommentIds = await _db.TrackComments
                .IgnoreQueryFilters()
                .Where(c => trackIdStrings.Contains(c.TrackId))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            if (orphanedCommentIds.Count > 0)
            {
                var deletedLikes = await _db.TrackCommentLikes
                    .Where(cl => orphanedCommentIds.Contains(cl.CommentId))
                    .ExecuteDeleteAsync(cancellationToken);

                var deletedComments = await _db.TrackComments
                    .IgnoreQueryFilters()
                    .Where(c => trackIdStrings.Contains(c.TrackId))
                    .ExecuteDeleteAsync(cancellationToken);

                _logger.LogInformation("Видалено {Comments} коментар(ів) та {Likes} лайк(ів) для остаточно видалених треків.",
                    deletedComments, deletedLikes);
            }

            await _db.Downloads
                .Where(d => d.Type == DownloadType.Track && d.ItemId != null && trackIds.Contains(d.ItemId.Value))
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var track in oldTracks)
            {
                if (!track.IsExternal) 
                {
                    DeleteLocalFile(track.AudioRelativePath);
                    DeleteLocalFile(track.CoverImageRelativePath);
                }
            }

            _db.Tracks.RemoveRange(oldTracks);
            _logger.LogInformation("Жорстко видалено треків із бази: {Count}", oldTracks.Count);
        }

        var oldAlbums = await _db.Albums
            .IgnoreQueryFilters()
            .Where(a => a.IsDeleted && a.DeletedAt < expiryDate)
            .ToListAsync(cancellationToken);

        if (oldAlbums.Any())
        {
            var oldAlbumIds = oldAlbums.Select(a => a.Id).ToList();

            // AlbumTitle — денормалізоване поле без FK, БД його не чистить каскадом,
            // тому без явного очищення трек назавжди лишився б із назвою вже видаленого альбому.
            await _db.Tracks
                .Where(t => t.AlbumId != null && oldAlbumIds.Contains(t.AlbumId.Value))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.AlbumId, (Guid?)null)
                    .SetProperty(t => t.AlbumTitle, (string?)null), cancellationToken);

            foreach (var album in oldAlbums)
            {
                DeleteLocalFile(album.CoverImageRelativePath);
            }

            _db.Albums.RemoveRange(oldAlbums);
            _logger.LogInformation("Жорстко видалено альбомів із бази: {Count}", oldAlbums.Count);
        }

        var oldPlaylists = await _db.Playlists
            .IgnoreQueryFilters()
            .Where(p => p.IsDeleted && p.DeletedAt < expiryDate)
            .ToListAsync(cancellationToken);

        if (oldPlaylists.Any())
        {
            var playlistIds = oldPlaylists.Select(p => p.Id).ToList();

            await _db.Downloads
                .Where(d => d.Type == DownloadType.Playlist && d.ItemId != null && playlistIds.Contains(d.ItemId.Value))
                .ExecuteDeleteAsync(cancellationToken);

            _db.Playlists.RemoveRange(oldPlaylists);
            _logger.LogInformation("Жорстко видалено плейлистів із бази: {Count}", oldPlaylists.Count);
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Планову очистку кошика успішно завершено.");
    }

    private void DeleteLocalFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;

        try
        {
            var absolutePath = Path.Combine(_mediaStoragePath, relativePath.TrimStart('\\', '/'));

            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
                _logger.LogDebug("Файл успішно видалено з диска: {Path}", absolutePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при спробі видалити файл: {RelativePath}", relativePath);
        }
    }
}