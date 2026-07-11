using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

public class GarbageCollectorService
{
    private readonly MusicDbContext _db;
    private readonly ILogger<GarbageCollectorService> _logger;
    private readonly string _mediaStoragePath;

    public GarbageCollectorService(
        MusicDbContext db, 
        IConfiguration configuration, 
        ILogger<GarbageCollectorService> logger)
    {
        _db = db;
        _logger = logger;
        
        // Извлекаем путь к файлам так же, как в Program.cs
        var basePathConfig = configuration["MediaStorage:BasePath"];
        _mediaStoragePath = string.IsNullOrWhiteSpace(basePathConfig)
            ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
            : Path.GetFullPath(basePathConfig);
    }

    public async Task CleanUpGarbageAsync(CancellationToken cancellationToken = default)
    {
        // Каждая запись живет в корзине ровно 30 дней с момента удаления
        //var expiryDate = DateTime.UtcNow.AddDays(-30);
        var expiryDate = DateTime.UtcNow.AddMinutes(2);

        _logger.LogInformation("Запуск планової очистки кошика Hangfire. Поріг дати: {ExpiryDate}", expiryDate);

        // 1. ОЧИСТКА ТРЕКОВ (Самая сложная часть из-за файлов и связей)
        var oldTracks = await _db.Tracks
            .IgnoreQueryFilters()
            .Where(t => t.IsDeleted && t.DeletedAt < expiryDate)
            .ToListAsync(cancellationToken);

        if (oldTracks.Any())
        {
            var trackIds = oldTracks.Select(t => t.Id).ToList();

            // Чистим связи в PlaylistTracks, так как там Restriction на удаление трека
            var linkedPlaylistTracks = await _db.PlaylistTracks
                .Where(pt => trackIds.Contains(pt.TrackId))
                .ToListAsync(cancellationToken);
                
            if (linkedPlaylistTracks.Any())
            {
                _db.PlaylistTracks.RemoveRange(linkedPlaylistTracks);
                _logger.LogInformation("Видалено {Count} зв'язків треків із плейлистами.", linkedPlaylistTracks.Count);
            }

            // Удаляем физические файлы с диска
            foreach (var track in oldTracks)
            {
                if (!track.IsExternal) // Ссылки на внешние ресурсы (стриминг) не трогаем
                {
                    DeleteLocalFile(track.AudioRelativePath);
                    DeleteLocalFile(track.CoverImageRelativePath);
                }
            }

            _db.Tracks.RemoveRange(oldTracks);
            _logger.LogInformation("Жорстко видалено треків із бази: {Count}", oldTracks.Count);
        }

        // 2. ОЧИСТКА АЛЬБОМОВ
        // Связи треков с альбомами настроены как OnDelete(DeleteBehavior.SetNull), 
        // поэтому SQL Server сам занулит AlbumId у живых треков при удалении альбома.
        var oldAlbums = await _db.Albums
            .IgnoreQueryFilters()
            .Where(a => a.IsDeleted && a.DeletedAt < expiryDate)
            .ToListAsync(cancellationToken);

        if (oldAlbums.Any())
        {
            _db.Albums.RemoveRange(oldAlbums);
            _logger.LogInformation("Жорстко видалено альбомів із бази: {Count}", oldAlbums.Count);
        }

        // 3. ОЧИСТКА ПЛЕЙЛИСТОВ
        // Связи PlaylistTracks настроены на Cascade при удалении Playlist, 
        // но для надежности и явности вычистим их.
        var oldPlaylists = await _db.Playlists
            .IgnoreQueryFilters()
            .Where(p => p.IsDeleted && p.DeletedAt < expiryDate)
            .ToListAsync(cancellationToken);

        if (oldPlaylists.Any())
        {
            _db.Playlists.RemoveRange(oldPlaylists);
            _logger.LogInformation("Жорстко видалено плейлистів із бази: {Count}", oldPlaylists.Count);
        }

        // Сохраняем все изменения одной транзакцией
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Планову очистку кошика успішно завершено.");
    }

    private void DeleteLocalFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;

        try
        {
            // Собираем абсолютный путь к файлу на сервере
            var absolutePath = Path.Combine(_mediaStoragePath, relativePath.TrimStart('\\', '/'));

            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
                _logger.LogDebug("Файл успішно видалено з диска: {Path}", absolutePath);
            }
        }
        catch (Exception ex)
        {
            // Ошибка удаления файла не должна прерывать очистку БД
            _logger.LogError(ex, "Помилка при спробі видалити файл: {RelativePath}", relativePath);
        }
    }
}