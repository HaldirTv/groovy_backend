using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

public class FavoritesService
{
    private readonly MusicDbContext _context;

    public FavoritesService(MusicDbContext context)
    {
        _context = context;
    }

    public async Task<bool> AddToFavoritesAsync(Guid userId, Guid trackId)
    {
        // 1. Проверяем, существует ли вообще такой трек в нашей базе
        var trackExists = await _context.Tracks.AnyAsync(t => t.Id == trackId);
        if (!trackExists) return false;

        // 2. Проверяем, не лайкнут ли он уже
        var alreadyExists = await _context.FavoriteTracks
            .AnyAsync(f => f.UserId == userId && f.TrackId == trackId);
        if (alreadyExists) return false;

        var favorite = new FavoriteTrack
        {
            UserId = userId,
            TrackId = trackId
        };

        _context.FavoriteTracks.Add(favorite);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveFromFavoritesAsync(Guid userId, Guid trackId)
    {
        var favorite = await _context.FavoriteTracks
            .FirstOrDefaultAsync(f => f.UserId == userId && f.TrackId == trackId);

        if (favorite == null) return false;

        _context.FavoriteTracks.Remove(favorite);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<Track>> GetUserFavoriteTracksAsync(Guid userId)
    {
        return await _context.FavoriteTracks
            .Where(f => f.UserId == userId)
            .Include(f => f.Track)
            .Select(f => f.Track!)
            .ToListAsync();
    }
    public async Task<HashSet<Guid>> GetLikedTrackIdsAsync(
        Guid userId, CancellationToken token = default)
    {
        var ids = await _context.FavoriteTracks
            .Where(f => f.UserId == userId)
            .Select(f => f.TrackId)
            .ToListAsync(token);
    
        return ids.ToHashSet();
    }
}