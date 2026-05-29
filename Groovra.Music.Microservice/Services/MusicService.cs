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
    public async Task<IReadOnlyList<Track>> GetAllTracksAsync(string? searchTerm = null, CancellationToken cancellationToken = default)
    {   
        var query = _db.Tracks.AsQueryable();

        // Если юзер что-то ввел в поиск — фильтруем
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(t => 
                t.Title.Contains(searchTerm) || 
                t.ArtistName.Contains(searchTerm));
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

    // ─── Delete ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Удаляет трек из БД и удаляет связанные файлы с диска.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> — трек найден и удалён;<br/>
    /// <see langword="false"/> — трек с указанным <paramref name="id"/> не найден.
    /// </returns>
    public async Task<bool> DeleteTrackAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var track = await _db.Tracks.FindAsync([id], cancellationToken);

        if (track is null)
        {
            _logger.LogWarning("Delete: трек {TrackId} не найден.", id);
            return false;
        }

        // Удаляем аудио-файл
        DeleteFileIfExists(Path.Combine(_mediaBasePath, track.AudioRelativePath));

        // Удаляем обложку (если была)
        if (track.CoverImageRelativePath is not null)
            DeleteFileIfExists(Path.Combine(_mediaBasePath, track.CoverImageRelativePath));

        _db.Tracks.Remove(track);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Трек удалён. Id={TrackId}, Title={Title}", id, track.Title);
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
        CancellationToken cancellationToken = default)
    {
        var track = await _db.Tracks.FindAsync([id], cancellationToken);

        if (track is null)
        {
            _logger.LogWarning("Rename: трек {TrackId} не найден.", id);
            return null;
        }

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
