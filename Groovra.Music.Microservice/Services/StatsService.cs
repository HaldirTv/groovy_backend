using Groovra.Music.Microservice.Model;
using Groovra.Shared.ServiceResult;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.DTOs;

public class StatsService
{
    private readonly MusicDbContext _db;

    public StatsService(MusicDbContext db)
    {
        _db = db;
    }
    public async Task<ServiceResult<ArtistStatsDto>> GetArtistDashboardStatsAsync(
        Guid artistUserId, 
        string baseUrl, 
        CancellationToken cancellationToken = default)
    {
        var totalPlays = await _db.Tracks
            .Where(t => t.UserId == artistUserId)
            .SumAsync(t => t.PlayCount, cancellationToken);

        var topTracks = await _db.Tracks
            .Where(t => t.UserId == artistUserId)
            .OrderByDescending(t => t.PlayCount)
            .Take(3)
            .AsNoTracking()
            .Select(t => new TopTrackDto
            {
                TrackId = t.Id,
                Title = t.Title,
                PlayCount = t.PlayCount,
                CoverImageUrl = t.IsExternal 
                    ? t.ExternalCoverUrl 
                    : !string.IsNullOrWhiteSpace(t.CoverImageRelativePath) 
                        ? $"{baseUrl}/music/files/{t.CoverImageRelativePath.Replace('\\', '/')}" 
                        : null
            })
            .ToListAsync(cancellationToken);

        var totalAlbumLikes = await _db.FavoriteAlbums
            .Include(fa => fa.Album)
            .Where(fa => fa.Album.UserId == artistUserId && !fa.Album.IsDeleted)
            .CountAsync(cancellationToken);

        var stats = new ArtistStatsDto
        {
            TotalPlayCount = totalPlays,
            TotalAlbumLikes = totalAlbumLikes, 
            TopTracks = topTracks
        };

        return ServiceResult<ArtistStatsDto>.Ok(stats);
    }

    public async Task<ServiceResult<GlobalStatsDto>> GetGlobalStatsAsync(CancellationToken cancellationToken = default)
    {
        var aiMixesCount = await _db.Playlists
            .Where(p => !p.IsDeleted && (p.Title.Contains("ШІ Мікс") || p.Title.Contains("AI Mix") || p.Slug.Contains("ai-mix")))
            .CountAsync(cancellationToken);

        var songsCount = await _db.Tracks
            .Where(t => !t.IsDeleted)
            .CountAsync(cancellationToken);

        var albumsCount = await _db.Albums
            .Where(a => !a.IsDeleted)
            .CountAsync(cancellationToken);

        var totalSecondsListened = await _db.Tracks
            .Where(t => !t.IsDeleted)
            .SumAsync(t => (long)t.DurationSeconds * t.PlayCount, cancellationToken);
        var hoursListened = (int)(totalSecondsListened / 3600);

        var stats = new GlobalStatsDto
        {
            AiMixesCount = aiMixesCount,
            SongsCount = songsCount,
            AlbumsCount = albumsCount,
            HoursListened = hoursListened
        };

        return ServiceResult<GlobalStatsDto>.Ok(stats);
    }
}