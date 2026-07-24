using Microsoft.EntityFrameworkCore;
using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Shared.ServiceResult;
using Groovra.Music.Microservice.Result;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;

namespace Groovra.Music.Microservice.Services;

/// <summary>
/// Сервіс для управління альбомами: створення, редагування, прив'язка/відв'язка треків, видалення.
/// </summary>
public class AlbumService
{
    private readonly MusicDbContext _context;
    private readonly ILogger<AlbumService> _logger;
    private readonly UploadService _uploadService;
    private readonly ICacheService _cache;

    public AlbumService(MusicDbContext context, UploadService uploadService, ICacheService cache, ILogger<AlbumService> logger)
    {
        _context = context;
        _uploadService = uploadService;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Скидає кеш пошуку альбомів і артистів (список артистів рахує AlbumCount
    /// із таблиці Albums) — викликається з кожного методу, що змінює каталог альбомів.</summary>
    private async Task InvalidateAlbumsCacheAsync(CancellationToken cancellationToken)
    {
        await _cache.RemoveByPatternAsync(CacheKeys.AlbumsSearchPatternAll, cancellationToken);
        await _cache.RemoveByPatternAsync(CacheKeys.ArtistsSearchPatternAll, cancellationToken);
    }
    
    // ─── Create ──────────────────────────────────────────────────────────────
    public async Task<ServiceResult<(AlbumDto Album, BulkTrackOperationResult TrackDetails)>> CreateAlbumAsync(
        Guid ownerUserId, string artistName, CreateAlbumDto dto, string baseUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Title))
            return ServiceResult<(AlbumDto, BulkTrackOperationResult)>.Fail("Назва альбому не може бути порожньою.");

        var albumId = Guid.NewGuid();
        string? coverRelativePath = null;

        if (dto.CoverFile != null && dto.CoverFile.Length > 0)
        {
            try
            {
                coverRelativePath = await _uploadService.UploadAlbumCoverAsync(dto.CoverFile, albumId, cancellationToken);
            }
            catch (Exception ex)
            {
                return ServiceResult<(AlbumDto, BulkTrackOperationResult)>.Fail($"Помилка при завантаженні обкладинки: {ex.Message}");
            }
        }

        var album = new Album
        {
            Id                   = albumId,
            UserId               = ownerUserId,
            Title                = dto.Title.Trim(),
            ArtistName           = artistName.Trim(),
            Description          = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            ReleaseDate          = dto.ReleaseDate,
            CoverImageRelativePath = coverRelativePath, 
            CreatedAt            = DateTime.UtcNow,
            UpdatedAt            = DateTime.UtcNow,
            IsDeleted            = false,
            Tracks               = new List<Track>(),
            TrackCount           = 0,
            TotalDurationSeconds = 0
        };

        _context.Albums.Add(album);

        var trackResult = new BulkTrackOperationResult();
        
        // ИСПРАВЛЕНО: Никаких ParsedTrackIds. Берем чистый TrackIds из DTO.
        if (dto.TrackIds != null && dto.TrackIds.Any())
        {
            trackResult = await ProcessTrackAssignmentAsync(album, dto.TrackIds, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);

        var albumDto = MapToDto(album, album.Tracks.ToList(), baseUrl, false);
        return ServiceResult<(AlbumDto, BulkTrackOperationResult)>.Ok((albumDto, trackResult));
    }
    // ─── Update ──────────────────────────────────────────────────────────────
    public async Task<ServiceResult<bool>> UpdateAlbumAsync(
        Guid albumId, Guid userId, UpdateAlbumDto dto, string baseUrl, CancellationToken cancellationToken = default)
    {
        // Добавлено: проверка !a.IsDeleted
        var album = await _context.Albums.FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
        if (album is null) return ServiceResult<bool>.Fail("Альбом не знайдено.");

        bool titleChanged = false;

        if (!string.IsNullOrWhiteSpace(dto.Title) && album.Title != dto.Title.Trim())
        {
            album.Title = dto.Title.Trim();
            titleChanged = true;
        }

        if (dto.Description is not null)
            album.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();

        if (dto.ReleaseDate is not null)
            album.ReleaseDate = dto.ReleaseDate;

        if (dto.CoverFile != null && dto.CoverFile.Length > 0)
        {
            try
            {
                album.CoverImageRelativePath = await _uploadService.UploadAlbumCoverAsync(dto.CoverFile, album.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                return ServiceResult<bool>.Fail($"Помилка при оновленні обкладинки: {ex.Message}");
            }
        }

        album.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        if (titleChanged)
        {
            await _context.Tracks
                .Where(t => t.AlbumId == albumId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.AlbumTitle, album.Title), cancellationToken);
        }

        await InvalidateAlbumsCacheAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── Логика дефолтной обложки ───────────────────────────────────────────
    private static string BuildCoverUrl(Album album, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(album.CoverImageRelativePath))
        {
            return "https://img.jamendo.com/albums/default.png";
        }

        var normalizedPath = album.CoverImageRelativePath.Replace('\\', '/');
        return $"{baseUrl}/music/files/{normalizedPath}";
    }

    // ─── Колаж із перших 4 обкладинок треків (для UI, як у плейлистів) ─────────
    private static List<string> BuildCollageCovers(IEnumerable<Track> tracks, string baseUrl)
    {
        return tracks
            .Take(4)
            .Select(t => t.IsExternal
                ? t.ExternalCoverUrl
                : !string.IsNullOrWhiteSpace(t.CoverImageRelativePath)
                    ? $"{baseUrl}/music/files/{t.CoverImageRelativePath.Replace('\\', '/')}"
                    : null)
            .Where(url => url != null)
            .Select(url => url!)
            .ToList();
    }

    // ─── GetRaw (для перевірки прав доступу у контролері) ──────────────────────
    public async Task<Album?> GetRawAlbumAsync(Guid albumId, CancellationToken cancellationToken = default)
    {
        return await _context.Albums
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
    }

    // ─── GetById ────────────────────────────────────────────────────────────────
    public async Task<ServiceResult<AlbumDto>> GetAlbumByIdAsync(
        Guid albumId,
        string baseUrl,
        bool isLiked,
        CancellationToken cancellationToken = default)
    {
        var album = await _context.Albums
            .Include(a => a.Tracks.OrderBy(t => t.UploadedAt))
            .FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);

        if (album is null)
            return ServiceResult<AlbumDto>.Fail("Альбом не знайдено.");

        return ServiceResult<AlbumDto>.Ok(MapToDto(album, album.Tracks.ToList(), baseUrl, isLiked));
    }

    // ─── List / Search ──────────────────────────────────────────────────────────
    public async Task<(IReadOnlyList<AlbumListItemDto> Items, int TotalCount)> GetAlbumsAsync(
        Guid? artistUserId,
        string? searchTerm,
        HashSet<Guid> likedAlbumIds,
        string baseUrl,
        string? genre = null,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // ФИКС: Убрали удаленные альбомы из списков
        var query = _context.Albums.AsNoTracking().Where(a => !a.IsDeleted).AsQueryable();

        if (artistUserId.HasValue)
            query = query.Where(a => a.UserId == artistUserId.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
            query = query.Where(a => a.Title.Contains(searchTerm) || a.ArtistName.Contains(searchTerm));

        if (!string.IsNullOrWhiteSpace(genre))
        {
            // БД вже регістронезалежна (Cyrillic_General_CI_AS) — без .ToLower(), інакше
            // предикат несаргабельний і індекс по Genre використати неможливо.
            var trimmedGenre = genre.Trim();
            query = query.Where(a => a.Tracks.Any(t => t.Genre != null && t.Genre == trimmedGenre));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // БЕЗ .Include(a => a.Tracks.OrderBy(...).Take(4)) — цей патерн генерує
        // CROSS/OUTER APPLY з ORDER BY+TOP на КОЖЕН альбом-рядок, що просить у SQL Server
        // memory grant під сортування. Саме це (а не обсяг даних — усього 3 альбоми в БД)
        // спричиняло стабільні ~25с RESOURCE_SEMAPHORE-очікування на кожен виклик пошуку
        // альбомів. Замість цього — прості альбоми одним запитом, а колаж обкладинок
        // окремим дешевим WHERE-IN запитом лише для сторінки результатів (без per-row сорту).
        var albums = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.Id) // стабільний тайбрейкер на випадок збігу CreatedAt (масовий імпорт)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var albumIds = albums.Select(a => a.Id).ToList();
        var collageByAlbum = albumIds.Count == 0
            ? new Dictionary<Guid, List<Track>>()
            : (await _context.Tracks
                    .AsNoTracking()
                    .Where(t => t.AlbumId != null && albumIds.Contains(t.AlbumId.Value))
                    .OrderBy(t => t.UploadedAt)
                    .ToListAsync(cancellationToken))
                .GroupBy(t => t.AlbumId!.Value)
                .ToDictionary(g => g.Key, g => g.Take(4).ToList());

        var items = albums.Select(a => new AlbumListItemDto
        {
            Id                   = a.Id,
            Title                = a.Title,
            ArtistName           = a.ArtistName,
            CoverImageUrl        = BuildCoverUrl(a, baseUrl),
            TrackCount           = a.TrackCount,
            TotalDurationSeconds = a.TotalDurationSeconds,
            ReleaseDate          = a.ReleaseDate,
            IsLiked              = likedAlbumIds.Contains(a.Id),
            CollageCovers        = BuildCollageCovers(collageByAlbum.GetValueOrDefault(a.Id, []), baseUrl),
        }).ToList();

        return (items, totalCount);
    }

    // ─── AddTracks (Bulk) ──────────────────────────────────────────────────────────
    public async Task<BulkTrackOperationResult> AddTracksToAlbumAsync(
        Guid albumId, List<Guid> trackIds, CancellationToken cancellationToken = default)
    {
        var album = await _context.Albums
            .Include(a => a.Tracks)
            .FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
            
        if (album is null) 
            return new BulkTrackOperationResult { IsAlbumNotFound = true };

        // Знову використовуємо універсальний рушій!
        var result = await ProcessTrackAssignmentAsync(album, trackIds, cancellationToken);

        if (result.HasChanges)
        {
            album.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            await InvalidateAlbumsCacheAsync(cancellationToken);
        }

        return result;
    }
    private async Task<BulkTrackOperationResult> ProcessTrackAssignmentAsync(
        Album album, List<Guid> requestedTrackIds, CancellationToken cancellationToken)
    {
        var result = new BulkTrackOperationResult();
        var uniqueTrackIds = requestedTrackIds.Distinct().ToList();
        
        // Дістаємо тільки треки, які належать автору альбому
        var tracks = await _context.Tracks
            .Where(t => uniqueTrackIds.Contains(t.Id) && t.UserId == album.UserId)
            .ToListAsync(cancellationToken);

        var foundTrackIds = tracks.Select(t => t.Id).ToHashSet();
        
        // Треки, яких немає в БД або вони чужі
        result.NotFoundIds = uniqueTrackIds.Where(id => !foundTrackIds.Contains(id)).ToList();

        // Завантажимо поточні айдішники треків цільового альбому для надійної перевірки
        var existingTrackIdsInAlbum = album.Tracks?.Select(t => t.Id).ToHashSet() ?? new HashSet<Guid>();

        foreach (var track in tracks)
        {
            // 1. ЖЕЛЕЗОБЕТОННАЯ ПРОВЕРКА: чи вже є трек саме в ЦЬОМУ альбомі
            // Перевіряємо і по явному співпадінню GUID, і по наявності в колекції альбому
            if ((track.AlbumId.HasValue && track.AlbumId.Value == album.Id) || existingTrackIdsInAlbum.Contains(track.Id))
            {
                result.AlreadyInAlbumIds.Add(track.Id);
                continue;
            }

            // 2. Трек знаходиться в ЯКОМУСЬ ІНШОМУ альбомі
            if (track.AlbumId.HasValue && track.AlbumId.Value != album.Id)
            {
                result.BelongsToAnotherAlbumIds.Add(track.Id);
                continue;
            }

            // 3. Успішна прив'язка (трек повністю вільний)
            track.AlbumId = album.Id;
            track.AlbumTitle = album.Title; 
            
            // Захист від подвійного додавання в колекцію, щоб EF не здублював дані
            if (album.Tracks != null && !existingTrackIdsInAlbum.Contains(track.Id))
            {
                album.Tracks.Add(track);
            }
        
            album.TrackCount++;
            album.TotalDurationSeconds += track.DurationSeconds;
            result.AddedIds.Add(track.Id);
        }

        return result;
    }

    // ─── RemoveTrack ─────────────────────────────────────────────────────────────
    public async Task<ServiceResult<bool>> RemoveTrackFromAlbumAsync(
        Guid albumId, Guid trackId, CancellationToken cancellationToken = default)
    {
        var album = await _context.Albums.FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
        if (album is null) return ServiceResult<bool>.Fail("Альбом не знайдено.");

        var track = await _context.Tracks
            .FirstOrDefaultAsync(t => t.Id == trackId && t.AlbumId == albumId, cancellationToken);

        if (track is null) return ServiceResult<bool>.Fail("Трек не знайдено у цьому альбомі.");

        track.AlbumId = null;
        track.AlbumTitle = null; 

        album.TrackCount = Math.Max(0, album.TrackCount - 1);
        album.TotalDurationSeconds = Math.Max(0, album.TotalDurationSeconds - track.DurationSeconds);
        album.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── DeleteAlbum ─────────────────────────────────────────────────────────────
    public async Task<ServiceResult<bool>> DeleteAlbumAsync(
        Guid albumId,
        CancellationToken cancellationToken = default)
    {
        // 1. Находим сам альбом
        var album = await _context.Albums.FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
        if (album is null) 
            return ServiceResult<bool>.Fail("Альбом не знайдено.");

        var now = DateTime.UtcNow;

        // 2. Мягко удаляем ТОЛЬКО альбом. 
        // Треки вообще НЕ ТРОГАЕМ. Их AlbumId остается на месте!
        album.IsDeleted = true;
        album.DeletedAt = now;
        album.UpdatedAt = now;

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);

        _logger.LogInformation("Album soft-deleted. Tracks preserved. Id={Id}", albumId);
        return ServiceResult<bool>.Ok(true);
    }
    
    
    
    public async Task<IReadOnlyList<AlbumListItemDto>> GetDeletedAlbumsAsync(Guid userId, string baseUrl, CancellationToken cancellationToken = default)
    {
        var albums = await _context.Albums
            .IgnoreQueryFilters()
            .Where(a => a.IsDeleted && a.UserId == userId)
            .OrderByDescending(a => a.DeletedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return albums.Select(a => new AlbumListItemDto
        {
            Id                   = a.Id,
            Title                = a.Title,
            ArtistName           = a.ArtistName,
            CoverImageUrl        = BuildCoverUrl(a, baseUrl),
            TrackCount           = a.TrackCount,
            TotalDurationSeconds = a.TotalDurationSeconds,
            ReleaseDate          = a.ReleaseDate,
            IsLiked              = false
        }).ToList();
    }
    
    public async Task<ServiceResult<bool>> RestoreAlbumAsync(Guid albumId, Guid currentUserId, string userRoles, CancellationToken cancellationToken = default)
    {
        var album = await _context.Albums
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == albumId && a.IsDeleted, cancellationToken);

        if (album is null) 
            return ServiceResult<bool>.Fail("Видалений альбом не знайдено.");

        if (album.UserId != currentUserId && !userRoles.HasRole(AppRoles.Admin))
            return ServiceResult<bool>.Fail("Немає прав для відновлення цього альбому.");

        album.IsDeleted = false;
        album.DeletedAt = null;
        album.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── PermanentDelete ─────────────────────────────────────────────────────

    /// <summary>Остаточно видаляє вже soft-deleted альбом (з кошика) — БД-запис і обкладинку
    /// з диска — не чекаючи 30-денної фонової очистки (<see cref="GarbageCollectorService.CleanUpGarbageAsync"/>).
    /// Треки, що були в альбомі, НЕ чіпаємо (FK — SetNull), як і при звичайному soft-delete.</summary>
    public async Task<ServiceResult<bool>> PermanentlyDeleteAlbumAsync(
        Guid albumId, Guid currentUserId, string userRoles, CancellationToken cancellationToken = default)
    {
        var album = await _context.Albums
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == albumId && a.IsDeleted, cancellationToken);

        if (album is null)
            return ServiceResult<bool>.Fail("Видалений альбом не знайдено.");

        if (album.UserId != currentUserId && !userRoles.HasRole(AppRoles.Admin))
            return ServiceResult<bool>.Fail("Немає прав для остаточного видалення цього альбому.");

        _uploadService.DeleteRelativeFile(album.CoverImageRelativePath);

        _context.Albums.Remove(album);
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);

        _logger.LogInformation("Album permanently deleted. Id={Id}", albumId);
        return ServiceResult<bool>.Ok(true);
    }

    // ─── GenerateRandomAlbums (bulk seed для тестових даних) ───────────────────────
    public async Task<ServiceResult<List<AlbumDto>>> GenerateRandomAlbumsAsync(
        Guid ownerUserId, string artistName, int albumsCount, int tracksPerAlbum, 
        string? genre, string baseUrl, bool onlySystemTracks = true, 
        CancellationToken cancellationToken = default)
    {
        var createdAlbums = new List<AlbumDto>();
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        for (int i = 0; i < albumsCount; i++)
        {
            var album = new Album
            {
                Id = Guid.NewGuid(),
                UserId = ownerUserId,
                Title = $"Generated Album {DateTime.UtcNow:yyyyMMddHHmmss}-{i + 1}",
                ArtistName = artistName.Trim(),
                TrackCount = 0,
                TotalDurationSeconds = 0,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            // Добавляем альбом в контекст, но НЕ сохраняем ещё
            _context.Albums.Add(album);

            var fillResult = await FillAlbumWithRandomTracksAsync(
                album.Id, tracksPerAlbum, genre, baseUrl, onlySystemTracks, cancellationToken);

            if (!fillResult.Success)
            {
                _logger.LogWarning("Не удалось заполнить альбом {AlbumId}. Причина: {Reason}", album.Id, fillResult.ErrorMessage);
                // Откатываем транзакцию – все изменения в этом цикле отменятся
                await transaction.RollbackAsync(cancellationToken);
                return ServiceResult<List<AlbumDto>>.Fail($"Не удалось создать альбом: {fillResult.ErrorMessage}");
            }

            createdAlbums.Add(fillResult.Data!);
        }

        // Сохраняем все альбомы и треки одной транзакцией
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);

        _logger.LogInformation("Сгенерировано {Count} альбомов", createdAlbums.Count);
        return ServiceResult<List<AlbumDto>>.Ok(createdAlbums);
    }

    // ФИКС: Оставлена только ОДНА версия метода FillAlbumWithRandomTracksAsync
    public async Task<ServiceResult<AlbumDto>> FillAlbumWithRandomTracksAsync(
        Guid albumId, 
        int count, 
        string? genre, 
        string baseUrl, 
        bool onlySystemTracks = true,
        CancellationToken cancellationToken = default)
    {
        var album = await _context.Albums.FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
        if (album is null) 
            return ServiceResult<AlbumDto>.Fail("Альбом не знайдено.");

        var query = _context.Tracks.Where(t => t.AlbumId == null);

        if (onlySystemTracks)
        {
            query = query.Where(t => t.UserId == Guid.Empty);
        }

        if (!string.IsNullOrWhiteSpace(genre))
        {
            var trimmedGenre = genre.Trim();
            query = query.Where(t => t.Genre != null && t.Genre == trimmedGenre);
        }

        var randomTracks = await query
            .OrderBy(t => Guid.NewGuid())
            .Take(count)
            .ToListAsync(cancellationToken);

        if (!randomTracks.Any())
            return ServiceResult<AlbumDto>.Fail("В базі даних не знайдено вільних треків за вказаними критеріями.");

        foreach (var track in randomTracks)
        {
            track.AlbumId = album.Id;
            track.AlbumTitle = album.Title; 

            album.TrackCount++;
            album.TotalDurationSeconds += track.DurationSeconds;
        }

        album.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);

        var allAlbumTracks = await _context.Tracks
            .Where(t => t.AlbumId == albumId)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Album {AlbumId} automatically filled with {Count} random tracks. Genre filter: {Genre}, OnlySystemTracks: {OnlySystemTracks}", 
            albumId, randomTracks.Count, genre ?? "None", onlySystemTracks);

        return ServiceResult<AlbumDto>.Ok(MapToDto(album, allAlbumTracks, baseUrl, isLiked: false));
    }
    
    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static AlbumDto MapToDto(Album album, List<Track> tracks, string baseUrl, bool isLiked)
    {
        return new AlbumDto
        {
            Id                   = album.Id,
            UserId               = album.UserId,
            Title                = album.Title,
            ArtistName           = album.ArtistName,
            Description          = album.Description,
            CoverImageUrl        = BuildCoverUrl(album, baseUrl),
            ReleaseDate          = album.ReleaseDate,
            TrackCount           = album.TrackCount,
            TotalDurationSeconds = album.TotalDurationSeconds,
            CreatedAt            = album.CreatedAt,
            IsLiked              = isLiked,
            CollageCovers        = BuildCollageCovers(tracks, baseUrl),
            Tracks = tracks.Select(t => new AlbumTrackItemDto
            {
                TrackId         = t.Id,
                Title           = t.Title,
                ArtistName      = t.ArtistName,
                DurationSeconds = t.DurationSeconds,
                AudioUrl        = $"{baseUrl}/music/tracks/{t.Id}/stream",
                CoverImageUrl   = t.IsExternal
                    ? t.ExternalCoverUrl
                    : !string.IsNullOrWhiteSpace(t.CoverImageRelativePath)
                        ? $"{baseUrl}/music/files/{t.CoverImageRelativePath.Replace('\\', '/')}"
                        : null,
            }).ToList(),
        };
    }
}