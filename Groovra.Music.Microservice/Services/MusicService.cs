using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;
using Groovra.Shared.Extensions;
using Groovra.Shared.Constants;
using Groovra.Messaging.Contracts;
using MassTransit;

namespace Groovra.Music.Microservice.Services;

/// <summary>
/// Сервис для управления треками: получение списка, удаление, переименование, учёт прослушиваний.
/// </summary>
public class MusicService
{
    private readonly MusicDbContext _db;
    private readonly ILogger<MusicService> _logger;
    private readonly IPublishEndpoint _publishEndpoint;
    /// <summary>Абсолютный корневой путь хранилища медиафайлов.</summary>
    private readonly string _mediaBasePath;

    public MusicService(
        MusicDbContext db,
        IConfiguration configuration,
        ILogger<MusicService> logger,
        IPublishEndpoint publishEndpoint)
    {
        _db = db;
        _logger = logger;
        _publishEndpoint = publishEndpoint;

        var configured = configuration["MediaStorage:BasePath"];
        _mediaBasePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
            : Path.GetFullPath(configured);
    }

    // ─── GetAll ──────────────────────────────────────────────────────────────

    // ─── GetAll (теперь с умным поиском) ─────────────────────────────────────

    /// <summary>
    /// Возвращает треки. Если передан searchTerm, ищет частичное совпадение 
    /// по названию трека или имени артиста (как в YouTube).
    /// </summary>
    public async Task<(IReadOnlyList<Track> Items, int TotalCount)> GetAllTracksAsync(
        string? searchTerm = null, 
        Guid? userId = null, 
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


        if (userId.HasValue)
        {
            query = query.Where(t => t.UserId == userId.Value);
        }


        var totalCount = await query.CountAsync(cancellationToken);

 
        var items = await query
            .OrderByDescending(t => t.PlayCount)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    // ─── GetById (получение конкретного трека) ───────────────────────────────

    public async Task<Track?> GetTrackByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Tracks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    // ─── GetTrackFileInfo (путь на диске + MIME-тип для стриминга) ────────────

    /// <summary>
    /// Возвращает абсолютный путь к аудиофайлу трека на диске и его MIME-тип.
    /// Используется endpoint'ом стриминга для PhysicalFile с Range-поддержкой.
    /// </summary>
    /// <returns>
    /// Кортеж (абсолютный путь, contentType) или <see langword="null"/>, если трек не найден.
    /// </returns>
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

    // ─── Delete ──────────────────────────────────────────────────────────────
    

    public async Task<bool> DeleteTrackAsync(Guid trackId, 
    Guid currentUserId, 
    string userRoles, 
    CancellationToken cancellationToken)
{
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

    // ── Синхронизация с альбомом (если трек был частью альбома) ────────────
    if (track.AlbumId.HasValue)
    {
        await _db.Albums
            .Where(a => a.Id == track.AlbumId.Value)
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.TrackCount, a => a.TrackCount - 1)
                      .SetProperty(a => a.TotalDurationSeconds, a => a.TotalDurationSeconds - track.DurationSeconds)
                      .SetProperty(a => a.UpdatedAt, a => DateTime.UtcNow),
                cancellationToken);
    }

    // ── Синхронизация с плейлистами (удаляем трек из всех плейлистов) ──────
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

    // ── Чистим лайки трека ──────────────────────────────────────────────────
    await _db.FavoriteTracks
        .Where(f => f.TrackId == trackId)
        .ExecuteDeleteAsync(cancellationToken);

    DeleteFileIfExists(Path.Combine(_mediaBasePath, track.AudioRelativePath));

    if (track.CoverImageRelativePath is not null)
        DeleteFileIfExists(Path.Combine(_mediaBasePath, track.CoverImageRelativePath));

    _db.Tracks.Remove(track);
    await _db.SaveChangesAsync(cancellationToken);

    _logger.LogInformation("Трек успешно удалён. Id={TrackId}, Title={Title}", trackId, track.Title);
    return true;
}

    // ─── Rename ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Изменяет название трека.
    /// </summary>
    /// <returns>
    /// Обновлённый объект <see cref="Track"/> или <see langword="null"/>, если трек не найден.
    /// </returns>
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

        _logger.LogInformation(
            "Трек переименован. Id={TrackId}, '{OldTitle}' → '{NewTitle}'",
            id, oldTitle, track.Title);

        return track;
    }

    // ─── IncrementPlayCount ───────────────────────────────────────────────────

    /// <summary>
    /// Атомарно увеличивает счётчик прослушиваний трека на 1.
    /// Использует ExecuteUpdateAsync — обновляет только одно поле без загрузки сущности.
    /// </summary>
    /// <param name="trackId">GUID трека.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>True — счётчик обновлён; false — трек не найден.</returns>
    public async Task<bool> IncrementPlayCountAsync(Guid userId,Guid trackId, CancellationToken cancellationToken = default)
    {
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
        await _publishEndpoint.Publish(new TrackPlayedEvent(
            UserId: userId,
            TrackId: trackId,
            PlayedAt: DateTime.UtcNow
        ), cancellationToken);
        
        _logger.LogDebug("PlayCount увеличен для трека {TrackId}.", trackId);
        return true;
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

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
}
