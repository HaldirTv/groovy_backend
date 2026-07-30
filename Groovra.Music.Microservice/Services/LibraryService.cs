using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

public class LibraryService
{
    private readonly MusicDbContext _db;
    private readonly ILogger<LibraryService> _logger;

    public LibraryService(MusicDbContext db, ILogger<LibraryService> logger)
    {
        _db = db;
        _logger = logger;
    }
    public async Task<(IReadOnlyList<TrackDto> Items, int TotalCount)> GetUserLibraryAsync(
        Guid userId,
        string baseUrl,
        int pageNumber = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var favoriteTrackIds = await _db.FavoriteTracks
            .Where(f => f.UserId == userId)
            .Select(f => f.TrackId)
            .ToListAsync(cancellationToken);

        var ownUploadedTrackIds = await _db.Tracks
            .Where(t => t.UserId == userId && !t.IsExternal)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        var playlistTrackIds = await _db.PlaylistTracks
            .Where(pt => pt.Playlist!.UserId == userId)
            .Select(pt => pt.TrackId)
            .ToListAsync(cancellationToken);

        var allTrackIds = favoriteTrackIds
            .Union(ownUploadedTrackIds)
            .Union(playlistTrackIds)
            .Distinct()
            .ToList();

        if (allTrackIds.Count == 0)
        {
            _logger.LogInformation("GetUserLibrary: у юзера {UserId} пустая библиотека.", userId);
            return (Array.Empty<TrackDto>(), 0);
        }

        var likedIds = favoriteTrackIds.ToHashSet();

        var orderedTracks = await _db.Tracks
            .Where(t => allTrackIds.Contains(t.Id))
            .AsNoTracking()
            .OrderBy(t => t.Title)
            .ToListAsync(cancellationToken);

        _logger.LogDebug(
            "GetUserLibrary: юзер {UserId}, треков в библиотеке — {Count}.",
            userId, orderedTracks.Count);

        var page = orderedTracks
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(t => MapToDto(t, baseUrl, likedIds.Contains(t.Id)))
            .ToList();

        return (page, orderedTracks.Count);
    }

    private static TrackDto MapToDto(Track track, string baseUrl, bool isLiked)
    {
        string? coverUrl = null;
        if (track.IsExternal)
        {
            coverUrl = track.ExternalCoverUrl;
        }
        else if (!string.IsNullOrWhiteSpace(track.CoverImageRelativePath))
        {
            coverUrl = $"{baseUrl}/music/files/{track.CoverImageRelativePath.Replace('\\', '/')}";
        }

        return new TrackDto
        {
            TrackId = track.Id,
            Title = track.Title,
            ArtistName = track.ArtistName,
            Album = track.AlbumTitle,
            Genre = track.Genre,
            Mood = track.Mood,
            DurationSeconds = track.DurationSeconds,
            FileSizeBytes = track.FileSizeBytes,
            ContentType = track.ContentType,
            AudioUrl = $"{baseUrl}/music/tracks/{track.Id}/stream",
            CoverImageUrl = coverUrl,
            UploadedAt = track.UploadedAt,
            PlayCount = track.PlayCount,
            IsLiked = isLiked
        };
    }
}