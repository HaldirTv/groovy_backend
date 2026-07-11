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
            .IgnoreQueryFilters() 
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
}