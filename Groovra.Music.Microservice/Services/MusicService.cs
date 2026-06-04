using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

/// <summary>
/// Сервис для управления треками: получение списка, удаление, переименование.
/// </summary>
public class MusicService
{
    private readonly MusicDbContext _db;
    private readonly ILogger<MusicService> _logger;

    /// <summary>Абсолютный корневой путь хранилища медиафайлов.</summary>
    private readonly string _mediaBasePath;

    public MusicService(
        MusicDbContext db,
        IConfiguration configuration,
        ILogger<MusicService> logger)
    {
        _db = db;
        _logger = logger;

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
    public async Task<IReadOnlyList<Track>> GetAllTracksAsync(string? searchTerm = null, Guid? userId = null, CancellationToken cancellationToken = default)
    {   
        var query = _db.Tracks.AsQueryable();

        // Если юзер что-то ввел в поиск — фильтруем
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(t => 
                t.Title.Contains(searchTerm) || 
                t.ArtistName.Contains(searchTerm)); 
        }
        if(userId.HasValue)
        {
            query = query.Where(t => t.UserId == userId.Value);
        }

        return await query
            .OrderByDescending(t => t.UploadedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
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
        string userRole, 
        CancellationToken cancellationToken)
    {
        // Исправлено: ищем по правильному trackId
        var track = await _db.Tracks.FirstOrDefaultAsync(t => t.Id == trackId, cancellationToken);

        if (track is null)
        {
            _logger.LogWarning("Delete: трек {TrackId} не найден.", trackId);
            return false; // Вернет false, контроллер поймет это как 404
        }


        if (track.UserId != currentUserId && userRole != "Admin")
        {
            _logger.LogWarning("Security violation: Юзер {UserId} с ролью {Role} пытался удалить чужой трек {TrackId}", 
                currentUserId, userRole, trackId);
            
            throw new UnauthorizedAccessException("You do not have permission to delete this track.");
        }


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
        string userRole, 
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
        if(track.UserId != currentUserId && userRole != "Admin")
            throw new UnauthorizedAccessException("You do not have permission to rename this track.");

        var oldTitle = track.Title;
        track.Title = newTitle.Trim();

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Трек переименован. Id={TrackId}, '{OldTitle}' → '{NewTitle}'",
            id, oldTitle, track.Title);

        return track;
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
