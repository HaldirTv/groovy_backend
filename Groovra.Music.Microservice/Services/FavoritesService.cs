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

    /// <summary>
    /// Пагінований варіант <see cref="GetUserFavoriteTracksAsync"/> — для UI зі стрічкою
    /// улюблених треків, що підвантажується частинами (кнопка "Завантажити ще").
    /// </summary>
    public async Task<(IReadOnlyList<TrackDto> Items, int TotalCount)> GetUserFavoriteTracksPagedAsync(
        Guid userId, string baseUrl, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = _context.FavoriteTracks
            .Where(f => f.UserId == userId)
            .Include(f => f.Track)
            .OrderByDescending(f => f.LikedAt)
            .Select(f => f.Track!)
            .AsNoTracking();

        var totalCount = await query.CountAsync(cancellationToken);

        var tracks = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (tracks.Select(t => MapToDto(t, baseUrl)).ToList(), totalCount);
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
                .ThenInclude(a => a!.Tracks.OrderBy(t => t.UploadedAt).Take(4))
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
    
    
    // ─── Playlists ──────────────────────────────────────────────────────────

    public async Task<bool> AddPlaylistToFavoritesAsync(Guid userId, Guid playlistId)
    {
        // Проверяем, что плейлист существует, не удален и является публичным
        var playlistExists = await _context.Playlists
            .AnyAsync(p => p.Id == playlistId && !p.IsDeleted && !p.IsPrivate);
        if (!playlistExists) return false;

        var alreadyExists = await _context.FavoritePlaylists
            .AnyAsync(f => f.UserId == userId && f.PlaylistId == playlistId);
        if (alreadyExists) return false;

        _context.FavoritePlaylists.Add(new FavoritePlaylist
        {
            UserId     = userId,
            PlaylistId = playlistId
        });

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemovePlaylistFromFavoritesAsync(Guid userId, Guid playlistId)
    {
        var favorite = await _context.FavoritePlaylists
            .FirstOrDefaultAsync(f => f.UserId == userId && f.PlaylistId == playlistId);

        if (favorite == null) return false;

        _context.FavoritePlaylists.Remove(favorite);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<PlaylistListItemDto>> GetUserFavoritePlaylistsAsync(Guid userId, string baseUrl, CancellationToken token = default)
    {
        // Тянем только те, которые активны (не удалены)
        var playlists = await _context.Playlists
            .Where(p => _context.FavoritePlaylists.Any(f => f.UserId == userId && f.PlaylistId == p.Id) && !p.IsDeleted)
            .Include(p => p.Tracks)
            .ThenInclude(pt => pt.Track)
            .AsNoTracking()
            .ToListAsync(token);

        return playlists.Select(p => MapPlaylistToListItemDto(p, baseUrl, isLiked: true));
    }

    public async Task<HashSet<Guid>> GetLikedPlaylistIdsAsync(Guid userId, CancellationToken token = default)
    {
        var ids = await _context.FavoritePlaylists
            .Where(f => f.UserId == userId && !f.Playlist.IsDeleted)
            .Select(f => f.PlaylistId)
            .ToListAsync(token);

        return ids.ToHashSet();
    }
    
    // Вспомогательный маппер плейлиста с заполнением всех полей
    private static PlaylistListItemDto MapPlaylistToListItemDto(Playlist playlist, string baseUrl, bool isLiked)
    {
        var collageCovers = playlist.Tracks
            .OrderBy(pt => pt.Position)
            .Take(4)
            .Select(pt => pt.Track?.IsExternal == true
                ? pt.Track.ExternalCoverUrl
                : pt.Track?.CoverImageRelativePath != null
                    ? $"{baseUrl}/music/files/{pt.Track.CoverImageRelativePath.Replace('\\', '/')}"
                    : null)
            .Where(url => url != null)
            .Select(url => url!)
            .ToList();

        return new PlaylistListItemDto
        {
            Id                   = playlist.Id,
            Title                = playlist.Title,
            Description          = playlist.Description,
            IsPrivate            = playlist.IsPrivate,
            IsLiked              = isLiked,
            Slug                 = playlist.Slug ?? string.Empty,
            TrackCount           = playlist.TrackCount,
            TotalDurationSeconds = playlist.TotalDurationSeconds,
            CoverImageUrl        = playlist.CoverImageUrl,
            UpdatedAt            = playlist.UpdatedAt,
            CollageCovers        = collageCovers
        };
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
            CollageCovers = album.Tracks
                .Take(4)
                .Select(t => t.IsExternal
                    ? t.ExternalCoverUrl
                    : !string.IsNullOrWhiteSpace(t.CoverImageRelativePath)
                        ? $"{baseUrl}/music/files/{t.CoverImageRelativePath.Replace('\\', '/')}"
                        : null)
                .Where(url => url != null)
                .Select(url => url!)
                .ToList(),
        };
    }
}