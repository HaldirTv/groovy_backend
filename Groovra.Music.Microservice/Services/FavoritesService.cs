using Groovra.Music.Microservice.DTOs;
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

    // ─── Tracks ─────────────────────────────────────────────────────────────

    public async Task<bool> AddToFavoritesAsync(Guid userId, Guid trackId)
    {
        var trackExists = await _context.Tracks.AnyAsync(t => t.Id == trackId);
        if (!trackExists) return false;

        var alreadyExists = await _context.FavoriteTracks
            .AnyAsync(f => f.UserId == userId && f.TrackId == trackId);
        if (alreadyExists) return false;

        _context.FavoriteTracks.Add(new FavoriteTrack
        {
            UserId  = userId,
            TrackId = trackId
        });

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

    /// <summary>
    /// Повертає улюблені треки юзера як TrackDto з повними URL для аудіо та обкладинки.
    /// </summary>
    public async Task<IEnumerable<TrackDto>> GetUserFavoriteTracksAsync(Guid userId, string baseUrl)
    {
        var tracks = await _context.FavoriteTracks
            .Where(f => f.UserId == userId)
            .Include(f => f.Track)
            .Select(f => f.Track!)
            .AsNoTracking()
            .ToListAsync();

        return tracks.Select(t => MapToDto(t, baseUrl));
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

    // ─── Albums ─────────────────────────────────────────────────────────────

    public async Task<bool> AddAlbumToFavoritesAsync(Guid userId, Guid albumId)
    {
        var albumExists = await _context.Albums.AnyAsync(a => a.Id == albumId && !a.IsDeleted);
        if (!albumExists) return false;

        var alreadyExists = await _context.FavoriteAlbums
            .AnyAsync(f => f.UserId == userId && f.AlbumId == albumId);
        if (alreadyExists) return false;

        _context.FavoriteAlbums.Add(new FavoriteAlbum
        {
            UserId  = userId,
            AlbumId = albumId
        });

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveAlbumFromFavoritesAsync(Guid userId, Guid albumId)
    {
        var favorite = await _context.FavoriteAlbums
            .FirstOrDefaultAsync(f => f.UserId == userId && f.AlbumId == albumId);

        if (favorite == null) return false;

        _context.FavoriteAlbums.Remove(favorite);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Повертає улюблені альбоми юзера як AlbumListItemDto з повними URL для обкладинки.
    /// </summary>
    public async Task<IEnumerable<AlbumListItemDto>> GetUserFavoriteAlbumsAsync(Guid userId, string baseUrl)
    {
        var albums = await _context.FavoriteAlbums
            .Where(f => f.UserId == userId && ! f.Album.IsDeleted)
            .Include(f => f.Album)
            .Select(f => f.Album!)
            .AsNoTracking()
            .ToListAsync();

        return albums.Select(a => MapAlbumToListItemDto(a, baseUrl, isLiked: true));
    }     

    public async Task<HashSet<Guid>> GetLikedAlbumIdsAsync(
        Guid userId, CancellationToken token = default)
    {
        var ids = await _context.FavoriteAlbums
            .Where(f => f.UserId == userId && !f.Album.IsDeleted)
            .Select(f => f.AlbumId)
            .ToListAsync(token);

        return ids.ToHashSet();
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static TrackDto MapToDto(Track track, string baseUrl)
    {
        string? coverUrl = null;
        if (track.IsExternal)
        {
            coverUrl = track.ExternalCoverUrl;
        }
        else if (!string.IsNullOrWhiteSpace(track.CoverImageRelativePath))
        {
            // Нормалізуємо шлях: замінюємо backslash, прибираємо leading slash
            var normalizedPath = track.CoverImageRelativePath
                .Replace('\\', '/')
                .TrimStart('/');

            // Якщо шлях не починається з "covers/" — додаємо
            if (!normalizedPath.StartsWith("covers/"))
                normalizedPath = $"covers/{normalizedPath}";

            coverUrl = $"{baseUrl}/music/files/{normalizedPath}";
        }

        return new TrackDto
        {
            TrackId         = track.Id,
            Title           = track.Title,
            ArtistName      = track.ArtistName,
            Album           = track.AlbumTitle ?? track.Album?.Title,
            Genre           = track.Genre,
            DurationSeconds = track.DurationSeconds,
            FileSizeBytes   = track.FileSizeBytes,
            ContentType     = track.ContentType,
            AudioUrl        = $"{baseUrl}/music/tracks/{track.Id}/stream",
            CoverImageUrl   = coverUrl,
            UploadedAt      = track.UploadedAt,
            PlayCount       = track.PlayCount,
            IsLiked         = true
        };
    }

    private static AlbumListItemDto MapAlbumToListItemDto(Album album, string baseUrl, bool isLiked)
    {
        string? coverUrl = null;
        if (!string.IsNullOrWhiteSpace(album.CoverImageRelativePath))
        {
            var normalizedPath = album.CoverImageRelativePath.Replace('\\', '/').TrimStart('/');
            coverUrl = $"{baseUrl}/music/files/{normalizedPath}";
        }

        return new AlbumListItemDto
        {
            Id                   = album.Id,
            Title                = album.Title,
            ArtistName           = album.ArtistName,
            CoverImageUrl        = coverUrl,
            TrackCount           = album.TrackCount,
            TotalDurationSeconds = album.TotalDurationSeconds,
            ReleaseDate          = album.ReleaseDate,
            IsLiked              = isLiked,
        };
    }
}