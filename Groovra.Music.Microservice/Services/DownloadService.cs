using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

public class DownloadService
{
    private readonly MusicDbContext _db;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(MusicDbContext db, ILogger<DownloadService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<(bool Success, string? Error)> AddDownloadAsync(
        Guid userId, AddDownloadDto dto, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<DownloadType>(dto.Type, true, out var type))
            return (false, "Невірний тип завантаження.");

        switch (type)
        {
            case DownloadType.Track:
                {
                    if (dto.ItemId is null) return (false, "ItemId обов'язковий для треку.");
                    var exists = await _db.Tracks.AnyAsync(t => t.Id == dto.ItemId, cancellationToken);
                    if (!exists) return (false, "Трек не знайдено.");
                    break;
                }
            case DownloadType.Playlist:
                {
                    if (dto.ItemId is null) return (false, "ItemId обов'язковий для плейлиста.");
                    var exists = await _db.Playlists.AnyAsync(p => p.Id == dto.ItemId, cancellationToken);
                    if (!exists) return (false, "Плейлист не знайдено.");
                    break;
                }
            case DownloadType.Album:
                {
                    if (string.IsNullOrWhiteSpace(dto.AlbumName) || string.IsNullOrWhiteSpace(dto.ArtistName))
                        return (false, "AlbumName та ArtistName обов'язкові для альбому.");
                    var exists = await _db.Tracks.AnyAsync(
                        t => t.Album == dto.AlbumName && t.ArtistName == dto.ArtistName, cancellationToken);
                    if (!exists) return (false, "Альбом не знайдено.");
                    break;
                }
        }

        var alreadyExists = await _db.Downloads.AnyAsync(d =>
            d.UserId == userId && d.Type == type &&
            (type == DownloadType.Album
                ? d.AlbumName == dto.AlbumName && d.ArtistName == dto.ArtistName
                : d.ItemId == dto.ItemId),
            cancellationToken);

        if (alreadyExists) return (false, "Вже додано до завантажень.");

        _db.Downloads.Add(new Download
        {
            UserId = userId,
            Type = type,
            ItemId = type == DownloadType.Album ? null : dto.ItemId,
            AlbumName = type == DownloadType.Album ? dto.AlbumName : null,
            ArtistName = type == DownloadType.Album ? dto.ArtistName : null
        });

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Download added. UserId={UserId}, Type={Type}", userId, type);
        return (true, null);
    }

    public async Task<bool> RemoveDownloadAsync(
        Guid userId, DownloadType type, Guid? itemId, string? albumName, string? artistName,
        CancellationToken cancellationToken = default)
    {
        var entry = await _db.Downloads.FirstOrDefaultAsync(d =>
            d.UserId == userId && d.Type == type &&
            (type == DownloadType.Album
                ? d.AlbumName == albumName && d.ArtistName == artistName
                : d.ItemId == itemId),
            cancellationToken);

        if (entry is null) return false;

        _db.Downloads.Remove(entry);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<DownloadItemDto>> GetDownloadsAsync(
        Guid userId, string baseUrl, DownloadType? filterType = null, CancellationToken cancellationToken = default)
    {
        var query = _db.Downloads.Where(d => d.UserId == userId);
        if (filterType.HasValue)
            query = query.Where(d => d.Type == filterType.Value);

        var downloads = await query
            .OrderByDescending(d => d.DownloadedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var result = new List<DownloadItemDto>();

        foreach (var d in downloads)
        {
            switch (d.Type)
            {
                case DownloadType.Track:
                    {
                        var track = await _db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == d.ItemId, cancellationToken);
                        if (track is null) continue;

                        result.Add(new DownloadItemDto
                        {
                            Type = "track",
                            ItemId = track.Id,
                            Title = track.Title,
                            SubTitle = track.ArtistName,
                            CoverImageUrl = ResolveCoverUrl(track, baseUrl),
                            TrackCount = 1,
                            TotalDurationSeconds = track.DurationSeconds,
                            FileSizeBytes = track.FileSizeBytes,
                            AudioUrl = $"{baseUrl}/music/tracks/{track.Id}/stream",
                            DownloadedAt = d.DownloadedAt
                        });
                        break;
                    }
                case DownloadType.Playlist:
                    {
                        var playlist = await _db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == d.ItemId, cancellationToken);
                        if (playlist is null) continue;

                        result.Add(new DownloadItemDto
                        {
                            Type = "playlist",
                            ItemId = playlist.Id,
                            Title = playlist.Title,
                            SubTitle = $"{playlist.TrackCount} треків",
                            CoverImageUrl = playlist.CoverImageUrl,
                            TrackCount = playlist.TrackCount,
                            TotalDurationSeconds = playlist.TotalDurationSeconds,
                            DownloadedAt = d.DownloadedAt
                        });
                        break;
                    }
                case DownloadType.Album:
                    {
                        var albumTracks = await _db.Tracks
                            .Where(t => t.Album == d.AlbumName && t.ArtistName == d.ArtistName)
                            .AsNoTracking()
                            .ToListAsync(cancellationToken);
                        if (albumTracks.Count == 0) continue;

                        var firstTrack = albumTracks.First();
                        result.Add(new DownloadItemDto
                        {
                            Type = "album",
                            Title = d.AlbumName!,
                            SubTitle = d.ArtistName,
                            CoverImageUrl = ResolveCoverUrl(firstTrack, baseUrl),
                            TrackCount = albumTracks.Count,
                            TotalDurationSeconds = albumTracks.Sum(t => t.DurationSeconds),
                            FileSizeBytes = albumTracks.Sum(t => t.FileSizeBytes),
                            DownloadedAt = d.DownloadedAt
                        });
                        break;
                    }
            }
        }

        return result;
    }
    private static string? ResolveCoverUrl(Track track, string baseUrl)
    {
        if (track.IsExternal) return track.ExternalCoverUrl;
        if (!string.IsNullOrWhiteSpace(track.CoverImageRelativePath))
            return $"{baseUrl}/music/files/{track.CoverImageRelativePath.Replace('\\', '/')}";
        return null;
    }
}