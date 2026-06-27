using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Shared.ServiceResult;
using Groovra.Music.Microservice.Result;

namespace Groovra.Music.Microservice.Services;

public class PlaylistService
{
    private readonly MusicDbContext _context;
    private readonly ILogger<PlaylistService> _logger;

    public PlaylistService(MusicDbContext context, ILogger<PlaylistService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Playlist?> GetRawPlaylistAsync(Guid playlistId, CancellationToken cancellationToken = default)
    {
        return await _context.Playlists
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);
    }

    // ─── Create ──────────────────────────────────────────────────────────────

    public async Task<ServiceResult<PlaylistDto>> CreatePlaylistAsync(
        Guid userId,
        string title,
        string? description,
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

        _logger.LogInformation("Playlist created. Id={Id}, Slug={Slug}", playlist.Id, playlist.Slug);
        return ServiceResult<PlaylistDto>.Ok(MapToDto(playlist));
    }

    // ─── GetUserPlaylists ─────────────────────────────────────────────────────

    public async Task<ServiceResult<IReadOnlyList<PlaylistListItemDto>>> GetUserPlaylistsAsync(
        Guid targetUserId,
        bool includePrivate,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Playlists
            .AsNoTracking()
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
                Slug                 = p.Slug,
                TrackCount           = p.TrackCount,
                TotalDurationSeconds = (double)p.TotalDurationSeconds, // Каст int базы в double DTO
                CoverImageUrl        = p.CoverImageUrl,
                UpdatedAt            = p.UpdatedAt,
                CollageCovers = p.Tracks
                    .OrderBy(pt => pt.Position)
                    .Take(4)
                    .Select(pt => pt.Track!.IsExternal
                        ? pt.Track.ExternalCoverUrl
                        : pt.Track.CoverImageRelativePath)
                    .Where(url => url != null)
                    .ToList()!,
            })
            .ToListAsync(cancellationToken);

        return ServiceResult<IReadOnlyList<PlaylistListItemDto>>.Ok(result);
    }

    // ─── GetById ──────────────────────────────────────────────────────────────

    public async Task<ServiceResult<PlaylistDto>> GetPlaylistByIdAsync(
        Guid playlistId,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists
            .Include(p => p.Tracks.OrderBy(pt => pt.Position))
                .ThenInclude(pt => pt.Track)
            .FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);

        if (playlist is null)
            return ServiceResult<PlaylistDto>.Fail("Плейлист не знайдено.");

        return ServiceResult<PlaylistDto>.Ok(MapToDto(playlist));
    }

    // ─── UpdatePrivacy ────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> UpdatePrivacyAsync(
        Guid playlistId,
        bool isPrivate,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);
        if (playlist is null) return ServiceResult<bool>.Fail("Плейлист не знайдено.");

        playlist.IsPrivate = isPrivate;
        playlist.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return ServiceResult<bool>.Ok(true);
    }

    // ─── AddTrack ─────────────────────────────────────────────────────────────

    public async Task<AddTrackResult> AddTrackToPlaylistAsync(
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);
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
        return AddTrackResult.Added;
    }

    // ─── RemoveTrack ──────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> RemoveTrackFromPlaylistAsync(
        Guid playlistId,
        Guid trackId,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);
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
        _context.PlaylistTracks.Remove(entry);

        await _context.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.Position > removedPosition)
            .ExecuteUpdateAsync(
                s => s.SetProperty(pt => pt.Position, pt => pt.Position - 1),
                cancellationToken);

        playlist.TrackCount = Math.Max(0, playlist.TrackCount - 1);
        playlist.TotalDurationSeconds = Math.Max(0, playlist.TotalDurationSeconds - (int)Math.Round(trackDuration));
        playlist.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── ReorderTracks ────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> ReorderTracksAsync(
        Guid playlistId,
        IList<Guid> orderedTrackIds,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);
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

        return ServiceResult<bool>.Ok(true);
    }

    // ─── SoftDelete ───────────────────────────────────────────────────────────

    public async Task<ServiceResult<bool>> DeletePlaylistAsync(
        Guid playlistId,
        CancellationToken cancellationToken = default)
    {
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, cancellationToken);
        if (playlist is null) return ServiceResult<bool>.Fail("Плейлист не знайдено.");

        playlist.IsDeleted = true;
        playlist.DeletedAt = DateTime.UtcNow;
        playlist.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static PlaylistDto MapToDto(Playlist p)
    {
        return new PlaylistDto(
            p.Id,
            p.UserId,
            p.Title,
            p.Description,
            p.Slug,
            p.CoverImageUrl,
            p.TrackCount,
            (double)p.TotalDurationSeconds, 
            p.IsPrivate,
            p.CreatedAt,
            p.Tracks?.Select(pt => new PlaylistTrackDto(
                pt.TrackId,
                pt.Track?.Title ?? "Unknown",
                pt.Track?.ArtistName ?? "Unknown",
                pt.Position,
                pt.Track?.IsExternal == true ? pt.Track.ExternalCoverUrl : pt.Track?.CoverImageRelativePath,
                pt.Track?.DurationSeconds ?? 0
            )).ToList() ?? new List<PlaylistTrackDto>()
        );
    }

    private static string GenerateSlug(string title)
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