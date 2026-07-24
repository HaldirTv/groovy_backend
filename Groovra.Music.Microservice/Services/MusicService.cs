using Groovra.Music.Microservice.Caching;
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
    private readonly ICacheService _cache;
    /// <summary>Абсолютный корневой путь хранилища медиафайлов.</summary>
    private readonly string _mediaBasePath;

    public MusicService(
        MusicDbContext db,
        IConfiguration configuration,
        ILogger<MusicService> logger,
        IPublishEndpoint publishEndpoint,
        ICacheService cache)
    {
        _db = db;
        _logger = logger;
        _publishEndpoint = publishEndpoint;
        _cache = cache;

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
        string? genre = null,
        string? artist = null,
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

        if (!string.IsNullOrWhiteSpace(artist))
        {
            query = query.Where(t => t.ArtistName.Contains(artist));
        }

        if (!string.IsNullOrWhiteSpace(genre))
        {
            // БД (Cyrillic_General_CI_AS) уже регістронезалежна для порівняння рядків, тож
            // .ToLower() тут зайвий і, гірше, робить предикат несаргабельним — SQL Server не
            // може скористатись індексом по Genre, коли колонка обгорнута у LOWER(...).
            var trimmedGenre = genre.Trim();
            query = query.Where(t => t.Genre != null && t.Genre == trimmedGenre);
        }

        if (userId.HasValue)
        {
            query = query.Where(t => t.UserId == userId.Value);
        }


        var totalCount = await query.CountAsync(cancellationToken);

 
        var items = await query
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id) // стабільний тайбрейкер: без нього рядки з однаковим PlayCount
                                // (типово 0 для щойно імпортованих треків) не гарантують той самий
                                // порядок між запитами, і Skip/Take між сторінками може "плавати".
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    /// <summary>
    /// Возвращает реальные значения Genre, которые сейчас есть у не удалённых треков в БД —
    /// это то, что фактически кладёт импорт из Jamendo, а не какой-то захардкоженный список.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDistinctGenresAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Tracks
            .Where(t => t.Genre != null && t.Genre != "")
            .Select(t => t.Genre!)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Returns tracks ordered by play count, most-played first — backs the "Popular"/
    /// "Recommended" feed.
    /// </summary>
    public async Task<(IReadOnlyList<Track> Items, int TotalCount)> GetPopularTracksAsync(
        int pageNumber = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Tracks.AsQueryable();

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id) // той самий стабільний тайбрейкер, що й у GetAllTracksAsync.
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    /// <summary>
    /// Базовий (без ML) алгоритм для секції "Музика за стилем та настроєм": для кожної
    /// категорії з <see cref="MoodCatalog.FallbackGenres"/> підбирає до perMoodCount треків
    /// у три кроки, без дублікатів між кроками — точний Track.Mood, потім жанр-фолбек, потім
    /// просто найпопулярніші — щоб карусель ніколи не була порожньою, навіть якщо жоден трек
    /// ще не протегований вручну.
    /// </summary>
    public async Task<IReadOnlyList<(string Mood, IReadOnlyList<Track> Tracks)>> GetMoodRecommendationsAsync(
        int perMoodCount = 8,
        CancellationToken cancellationToken = default)
    {
        // ПРИМІТКА (діагностика продакшн-інциденту): раніше кожна з N категорій робила до
        // 3 послідовних SQL-запитів (до 3×N разом), причому тир-3 "просто найпопулярніші"
        // НЕ залежить від mood взагалі, але виконувався окремим ідентичним запитом на кожну
        // категорію. На малій тестовій базі (~3 треки) це було непомітно, але після імпорту
        // ~400 треків (жоден ще не протегований Mood, і жанри здебільшого не з каталогу)
        // майже всі 6 категорій падали саме в цей тир-3 фолбек — тобто один і той самий
        // "ORDER BY PlayCount DESC" запит виконувався до 6 разів підряд щоразу, коли
        // React StrictMode чи повторний виклик з фронтенду дублювали запит. Під навантаженням
        // (кілька конкурентних викликів одразу після рестарту сервісу, поки .NET thread pool
        // ще не "розігрівся") це й спричиняло помітні затримки/зависання ендпоінта — не
        // NullReferenceException, а банальна надлишкова кількість однакових запитів до БД.
        // Тепер — рівно 3 запити ЗАГАЛОМ, незалежно від кількості категорій: по одному на
        // кожен тир, розкладання по категоріях відбувається вже в пам'яті.
        var moodCount = MoodCatalog.FallbackGenres.Count;

        // Тир 1: усі протеговані треки — одним запитом, групуємо по mood у пам'яті.
        var taggedByMood = (await _db.Tracks
                .Where(t => t.Mood != null)
                .OrderByDescending(t => t.PlayCount)
                .ThenBy(t => t.Id)
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.Mood!.Trim().ToLower())
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Track>)g.ToList());

        // Тир 2: усі треки, чий Genre входить у ЖОДЕН з фолбек-переліків будь-якої категорії —
        // одним запитом; кожна категорія далі фільтрує з нього своїм переліком у пам'яті.
        // БД (Cyrillic_General_CI_AS) уже регістронезалежна, тож порівнюємо Genre як є, без
        // LOWER(...) — інакше предикат стає несаргабельним і не може скористатись індексом.
        var allFallbackGenres = MoodCatalog.FallbackGenres.Values
            .SelectMany(g => g)
            .Distinct()
            .ToArray();
        var genreCandidates = await _db.Tracks
            .Where(t => t.Genre != null && allFallbackGenres.Contains(t.Genre))
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Тир 3: глобальний фолбек за популярністю — не залежить від mood, тож рахуємо один
        // раз, із запасом (perMoodCount * moodCount), щоб вистачило, навіть якщо всі категорії
        // підряд потребуватимуть саме цей фолбек.
        var globalPopular = await _db.Tracks
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(t => t.Id)
            .Take(perMoodCount * moodCount)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var result = new List<(string, IReadOnlyList<Track>)>();

        foreach (var (mood, fallbackGenres) in MoodCatalog.FallbackGenres)
        {
            var picked = new List<Track>();
            var pickedIds = new HashSet<Guid>();

            if (taggedByMood.TryGetValue(mood.Trim().ToLower(), out var byMood))
            {
                picked.AddRange(byMood.Take(perMoodCount));
                pickedIds.UnionWith(picked.Select(t => t.Id));
            }

            if (picked.Count < perMoodCount)
            {
                var moodGenres = fallbackGenres.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var byGenre = genreCandidates
                    .Where(t => !pickedIds.Contains(t.Id) && t.Genre != null && moodGenres.Contains(t.Genre))
                    .Take(perMoodCount - picked.Count)
                    .ToList();
                picked.AddRange(byGenre);
                pickedIds.UnionWith(byGenre.Select(t => t.Id));
            }

            if (picked.Count < perMoodCount)
            {
                var filler = globalPopular
                    .Where(t => !pickedIds.Contains(t.Id))
                    .Take(perMoodCount - picked.Count);
                picked.AddRange(filler);
            }

            if (picked.Count > 0)
                result.Add((mood, picked));
        }

        return result;
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
    
    
    
    public async Task<IReadOnlyList<Track>> GetDeletedTracksAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.Tracks
            .IgnoreQueryFilters()
            .Where(t => t.IsDeleted && t.UserId == userId)
            .OrderByDescending(t => t.DeletedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
    
    public async Task<bool> RestoreTrackAsync(Guid trackId, Guid currentUserId, string userRoles, CancellationToken cancellationToken = default)
    {
        var track = await _db.Tracks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == trackId && t.IsDeleted, cancellationToken);
    
        if (track is null) return false;
    
        if (track.UserId != currentUserId && !userRoles.HasRole(AppRoles.Admin))
        {
            throw new UnauthorizedAccessException("You do not have permission to restore this track.");
        }
    
        track.IsDeleted = false;
        track.DeletedAt = null;
    
        
        if (track.AlbumId.HasValue)
        {
            var album = await _db.Albums
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.Id == track.AlbumId.Value && !a.IsDeleted, cancellationToken);
                
            if (album != null)
            {
                album.TrackCount++;
                album.TotalDurationSeconds += track.DurationSeconds;
                album.UpdatedAt = DateTime.UtcNow;
            }
        }
    
        await _db.SaveChangesAsync(cancellationToken);
        await InvalidateTrackCachesAsync(trackId, cancellationToken);
        return true;
    }

    // ─── Delete ──────────────────────────────────────────────────────────────
    

    public async Task<bool> DeleteTrackAsync(Guid trackId, 
    Guid currentUserId, 
    string userRoles, 
    CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

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

        // ── 1. Синхронизация с альбомом (ОСТАВЛЯЕМ) ───────────────────────────
        if (track.AlbumId.HasValue)
        {
            await _db.Albums
                .Where(a => a.Id == track.AlbumId.Value && !a.IsDeleted) 
                .ExecuteUpdateAsync(
                    s => s.SetProperty(a => a.TrackCount, a => a.TrackCount - 1)
                        .SetProperty(a => a.TotalDurationSeconds, a => a.TotalDurationSeconds - track.DurationSeconds)
                        .SetProperty(a => a.UpdatedAt, a => DateTime.UtcNow),
                    cancellationToken);
        }

        // ── 2. Синхронизация с плейлистами (ОСТАВЛЯЕМ) ────────────────────────
        // Из плейлистов трек убираем сразу, чтобы у пользователей не ломались кастомные списки
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

        // ── 3. ЛАЙКИ И ФАЙЛЫ НЕ ТРОГАЕМ ────────────────────────────────────────
        // Кнопку ExecuteDeleteAsync для FavoriteTracks и методы DeleteFileIfExists убираем.
        // Они остаются в базе, но за счет флага IsDeleted отфильтруются в FavoritesService.

        // ── 4. РЕАЛИЗАЦИЯ SOFT DELETE ──────────────────────────────────────────
        track.IsDeleted = true;
        track.DeletedAt = DateTime.UtcNow;

        // Никакого _db.Tracks.Remove(track)! Просто сохраняем изменения флагов.
        await _db.SaveChangesAsync(cancellationToken);

        // ── 5. ІВЕНТ ДЛЯ HISTORY ─────────────────────────────────────────────
        // History не має FK на Track (окремий сервіс/схема), тому без цього івенту
        // записи PlaybackHistory лишаються сиротами назавжди. Публікуємо до коміту
        // за тим самим патерном, що й TrackPlayedEvent у IncrementPlayCountAsync —
        // якщо публікація впаде, відкатуємо і soft-delete теж.
        try
        {
            await _publishEndpoint.Publish(new TrackDeletedEvent(trackId), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось опубликовать TrackDeletedEvent для трека {TrackId}. Откат транзакции.", trackId);
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        await transaction.CommitAsync(cancellationToken);
        await InvalidateTrackCachesAsync(trackId, cancellationToken);

        _logger.LogInformation("Трек успешно переведен в статус Soft-Deleted. Id={TrackId}, Title={Title}", trackId, track.Title);
        return true;
    }

    // ─── PermanentDelete ─────────────────────────────────────────────────────

    /// <summary>Остаточно видаляє вже soft-deleted трек — і з БД, і файли з диска — не
    /// чекаючи 30-денного вікна кошика (<see cref="GarbageCollectorService.CleanUpGarbageAsync"/>).
    /// Дозволено лише власнику або Admin, і лише для запису, який вже IsDeleted (для активного
    /// треку спершу потрібен звичайний <see cref="DeleteTrackAsync"/>).</summary>
    public async Task<bool> PermanentlyDeleteTrackAsync(
        Guid trackId, Guid currentUserId, string userRoles, CancellationToken cancellationToken = default)
    {
        var track = await _db.Tracks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == trackId && t.IsDeleted, cancellationToken);

        if (track is null) return false;

        if (track.UserId != currentUserId && !userRoles.HasRole(AppRoles.Admin))
            throw new UnauthorizedAccessException("You do not have permission to permanently delete this track.");

        // Той самий порядок дій, що й у GarbageCollectorService.CleanUpGarbageAsync,
        // тільки для одного треку за явним запитом користувача.
        var linkedPlaylistTracks = await _db.PlaylistTracks
            .Where(pt => pt.TrackId == trackId)
            .ToListAsync(cancellationToken);
        if (linkedPlaylistTracks.Any())
            _db.PlaylistTracks.RemoveRange(linkedPlaylistTracks);

        if (!track.IsExternal)
        {
            DeleteRelativeFileIfExists(track.AudioRelativePath);
            DeleteRelativeFileIfExists(track.CoverImageRelativePath);
        }

        _db.Tracks.Remove(track);
        await _db.SaveChangesAsync(cancellationToken);
        await InvalidateTrackCachesAsync(trackId, cancellationToken);

        _logger.LogInformation("Трек остаточно видалено (БД + диск). Id={TrackId}, Title={Title}", trackId, track.Title);
        return true;
    }

    private void DeleteRelativeFileIfExists(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        try
        {
            var absolutePath = Path.Combine(_mediaBasePath, relativePath.TrimStart('\\', '/'));
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при остаточному видаленні файлу: {RelativePath}", relativePath);
        }
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
        await InvalidateTrackCachesAsync(id, cancellationToken);

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
    public async Task<bool> IncrementPlayCountAsync(Guid userId, Guid trackId, CancellationToken cancellationToken = default)
    {
 
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

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

        try
        {

            await _publishEndpoint.Publish(new TrackPlayedEvent(
                UserId: userId,
                TrackId: trackId,
                PlayedAt: DateTime.UtcNow
            ), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось опубликовать TrackPlayedEvent для трека {TrackId}. Откат транзакции.", trackId);
            await transaction.RollbackAsync(cancellationToken);
            throw; // Пробрасываем ошибку выше для корректного ответа API (500)
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.LogDebug("PlayCount увеличен для трека {TrackId}.", trackId);
        return true;
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Сбрасывает кеш конкретного трека и все кеши списков (поиск/тренды/
    /// рекомендации), которые могли содержать его данные — вызывается при удалении,
    /// восстановлении и переименовании.</summary>
    private async Task InvalidateTrackCachesAsync(Guid trackId, CancellationToken cancellationToken)
    {
        await _cache.RemoveAsync(CacheKeys.Track(trackId), cancellationToken);
        foreach (var pattern in CacheKeys.ListPatterns)
        {
            await _cache.RemoveByPatternAsync(pattern, cancellationToken);
        }
    }

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
