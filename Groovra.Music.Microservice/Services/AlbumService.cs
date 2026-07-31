using Microsoft.EntityFrameworkCore;
using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Shared.ServiceResult;
using Groovra.Music.Microservice.Result;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;

namespace Groovra.Music.Microservice.Services;

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

    private async Task InvalidateAlbumsCacheAsync(CancellationToken cancellationToken)
    {
        await _cache.RemoveByPatternAsync(CacheKeys.AlbumsSearchPatternAll, cancellationToken);
        await _cache.RemoveByPatternAsync(CacheKeys.ArtistsSearchPatternAll, cancellationToken);
    }

    /// <summary>
    /// Операції над альбомом міняють і рядки треків (AlbumId/AlbumTitle), а трек кешується
    /// окремо — під music:track:{id} та у списках. Без цього скидання видалення альбому
    /// правильно відв'язувало трек у БД, але API ще довго віддавав закешований DTO зі
    /// старою назвою вже видаленого альбому. Викликати всюди, де змінюються треки.
    /// </summary>
    private async Task InvalidateTracksCacheAsync(IEnumerable<Guid> trackIds, CancellationToken cancellationToken)
    {
        foreach (var trackId in trackIds.Distinct())
        {
            await _cache.RemoveAsync(CacheKeys.Track(trackId), cancellationToken);
        }

        foreach (var pattern in CacheKeys.ListPatterns)
        {
            await _cache.RemoveByPatternAsync(pattern, cancellationToken);
        }
    }
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
        if (dto.TrackIds != null && dto.TrackIds.Any())
        {
            trackResult = await ProcessTrackAssignmentAsync(album, dto.TrackIds, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);
        await InvalidateTracksCacheAsync(trackResult.AddedIds, cancellationToken);
        var albumDto = MapToDto(album, album.Tracks.ToList(), baseUrl, false);
        return ServiceResult<(AlbumDto, BulkTrackOperationResult)>.Ok((albumDto, trackResult));
    }
    public async Task<ServiceResult<bool>> UpdateAlbumAsync(
        Guid albumId, Guid userId, UpdateAlbumDto dto, string baseUrl, CancellationToken cancellationToken = default)
    {
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
            var previousCoverPath = album.CoverImageRelativePath;
            try
            {
                album.CoverImageRelativePath = await _uploadService.UploadAlbumCoverAsync(dto.CoverFile, album.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                return ServiceResult<bool>.Fail($"Помилка при оновленні обкладинки: {ex.Message}");
            }

            // Нову обкладинку зберігаємо під іменем albumId_album_cover{ext}: якщо розширення
            // не змінилось, новий файл сам перезаписав старий; якщо змінилось — старий файл
            // лишився б осиротілим на диску, тому приберемо його явно.
            if (!string.IsNullOrWhiteSpace(previousCoverPath) && previousCoverPath != album.CoverImageRelativePath)
            {
                _uploadService.DeleteMediaFileIfExists(previousCoverPath);
            }
        }
        else if (dto.RemoveCover && !string.IsNullOrWhiteSpace(album.CoverImageRelativePath))
        {
            _uploadService.DeleteMediaFileIfExists(album.CoverImageRelativePath);
            album.CoverImageRelativePath = null;
        }

        album.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        if (titleChanged)
        {
            var renamedTrackIds = await _context.Tracks
                .Where(t => t.AlbumId == albumId)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            await _context.Tracks
                .Where(t => t.AlbumId == albumId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.AlbumTitle, album.Title), cancellationToken);

            await InvalidateTracksCacheAsync(renamedTrackIds, cancellationToken);
        }

        await InvalidateAlbumsCacheAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    private static string BuildCoverUrl(Album album, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(album.CoverImageRelativePath))
        {
            return "https://img.jamendo.com/albums/default.png";
        }

        var normalizedPath = album.CoverImageRelativePath.Replace('\\', '/');
        return $"{baseUrl}/music/files/{normalizedPath}";
    }

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

    public async Task<Album?> GetRawAlbumAsync(Guid albumId, CancellationToken cancellationToken = default)
    {
        return await _context.Albums
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
    }

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
        var query = _context.Albums.AsNoTracking().Where(a => !a.IsDeleted).AsQueryable();

        if (artistUserId.HasValue)
            query = query.Where(a => a.UserId == artistUserId.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
            query = query.Where(a => a.Title.Contains(searchTerm) || a.ArtistName.Contains(searchTerm));

        if (!string.IsNullOrWhiteSpace(genre))
        {
            var trimmedGenre = genre.Trim();
            query = query.Where(a => a.Tracks.Any(t => t.Genre != null && t.Genre == trimmedGenre));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var albums = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // Обложка-коллаж тянется отдельным дешёвым WHERE-IN запросом только для страницы
        // результатов (без per-row APPLY+ORDER BY+TOP, который на общей БД даёт заметный
        // memory grant даже при небольшом объёме данных).
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

    public async Task<BulkTrackOperationResult> AddTracksToAlbumAsync(
        Guid albumId, List<Guid> trackIds, CancellationToken cancellationToken = default)
    {
        var album = await _context.Albums
            .Include(a => a.Tracks)
            .FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
        if (album is null) 
            return new BulkTrackOperationResult { IsAlbumNotFound = true };

        var result = await ProcessTrackAssignmentAsync(album, trackIds, cancellationToken);

        if (result.HasChanges)
        {
            album.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            await InvalidateAlbumsCacheAsync(cancellationToken);
            await InvalidateTracksCacheAsync(result.AddedIds, cancellationToken);
        }

        return result;
    }
    private async Task<BulkTrackOperationResult> ProcessTrackAssignmentAsync(
        Album album, List<Guid> requestedTrackIds, CancellationToken cancellationToken)
    {
        var result = new BulkTrackOperationResult();
        var uniqueTrackIds = requestedTrackIds.Distinct().ToList();
        var tracks = await _context.Tracks
            .Where(t => uniqueTrackIds.Contains(t.Id) && t.UserId == album.UserId)
            .ToListAsync(cancellationToken);

        var foundTrackIds = tracks.Select(t => t.Id).ToHashSet();
        result.NotFoundIds = uniqueTrackIds.Where(id => !foundTrackIds.Contains(id)).ToList();

        var existingTrackIdsInAlbum = album.Tracks?.Select(t => t.Id).ToHashSet() ?? new HashSet<Guid>();

        // Альбоми, що блокують ці треки, можуть лежати в кошику — а кошик відфільтрований
        // глобальним !IsDeleted, тому дізнатись про них можна лише з IgnoreQueryFilters.
        var blockingAlbumIds = tracks
            .Where(t => t.AlbumId.HasValue && t.AlbumId.Value != album.Id)
            .Select(t => t.AlbumId!.Value)
            .Distinct()
            .ToList();

        var trashedAlbumIds = blockingAlbumIds.Count == 0
            ? new HashSet<Guid>()
            : (await _context.Albums
                .IgnoreQueryFilters()
                .Where(a => blockingAlbumIds.Contains(a.Id) && a.IsDeleted)
                .Select(a => a.Id)
                .ToListAsync(cancellationToken)).ToHashSet();

        foreach (var track in tracks)
        {
            if ((track.AlbumId.HasValue && track.AlbumId.Value == album.Id) || existingTrackIdsInAlbum.Contains(track.Id))
            {
                result.AlreadyInAlbumIds.Add(track.Id);
                continue;
            }

            if (track.AlbumId.HasValue && track.AlbumId.Value != album.Id)
            {
                if (trashedAlbumIds.Contains(track.AlbumId.Value))
                    result.BelongsToTrashedAlbumIds.Add(track.Id);
                else
                    result.BelongsToAnotherAlbumIds.Add(track.Id);
                continue;
            }

            track.AlbumId = album.Id;
            track.AlbumTitle = album.Title; 
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
        await InvalidateTracksCacheAsync([trackId], cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<bool>> DeleteAlbumAsync(
        Guid albumId,
        CancellationToken cancellationToken = default)
    {
        var album = await _context.Albums.FirstOrDefaultAsync(a => a.Id == albumId && !a.IsDeleted, cancellationToken);
        if (album is null) 
            return ServiceResult<bool>.Fail("Альбом не знайдено.");

        var now = DateTime.UtcNow;

        // ВАЖЛИВО: кошик НЕ розриває зв'язок альбом-трек. Раніше тут треки одразу
        // від'єднувались і TrackCount обнулявся — через це "відновити з кошика"
        // повертало порожній альбом, а склад із умовних 30 треків губився назавжди.
        // Тепер AlbumId/AlbumTitle лишаються недоторканими, доки альбом у кошику:
        // сам альбом просто зникає зі списків (глобальний фільтр !IsDeleted), а
        // відновлення повертає його рівно в тому ж складі.
        // Зв'язок рветься лише при остаточному видаленні — у PermanentlyDeleteAlbumAsync
        // та у 30-денному прибиранні GarbageCollectorService; саме там треки стають
        // вільними й доступними для вибору в новий альбом.
        album.IsDeleted = true;
        album.DeletedAt = now;
        album.UpdatedAt = now;

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);

        _logger.LogInformation(
            "Album moved to trash, {TrackCount} track link(s) preserved for restore. Id={Id}",
            album.TrackCount, albumId);
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

        // FavoriteAlbum прибирається на рівні БД (Cascade). Track.AlbumId теж мав би
        // прибратись через SetNull, але AlbumTitle — денормалізоване поле без FK, тому
        // БД його не чіпає: без явного очищення трек назавжди показував би застарілу
        // назву вже видаленого альбому і блокувався б у пікері інших альбомів.
        var freedTrackIds = await _context.Tracks
            .Where(t => t.AlbumId == albumId)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        await _context.Tracks
            .Where(t => t.AlbumId == albumId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.AlbumId, (Guid?)null)
                .SetProperty(t => t.AlbumTitle, (string?)null), cancellationToken);

        _uploadService.DeleteMediaFileIfExists(album.CoverImageRelativePath);

        _context.Albums.Remove(album);
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);
        await InvalidateTracksCacheAsync(freedTrackIds, cancellationToken);

        _logger.LogInformation("Album permanently deleted. Id={Id}", albumId);
        return ServiceResult<bool>.Ok(true);
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

        // Склад альбому пережив кошик недоторканим, але поки він там лежав, окремий трек
        // могли видалити самостійно (його власний soft-delete). Тому лічильники рахуємо
        // заново з реально живих треків, а не довіряємо збереженому значенню - інакше
        // відновлений альбом показував би "30 треків" там, де їх лишилось 29.
        var liveTracks = await _context.Tracks
            .Where(t => t.AlbumId == albumId)
            .Select(t => new { t.Id, t.DurationSeconds })
            .ToListAsync(cancellationToken);

        album.TrackCount = liveTracks.Count;
        album.TotalDurationSeconds = liveTracks.Sum(t => t.DurationSeconds);

        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);
        await InvalidateTracksCacheAsync(liveTracks.Select(t => t.Id), cancellationToken);

        _logger.LogInformation("Album restored from trash with {Count} track(s). Id={Id}", liveTracks.Count, albumId);
        return ServiceResult<bool>.Ok(true);
    }
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

            _context.Albums.Add(album);

            var fillResult = await FillAlbumWithRandomTracksAsync(
                album.Id, tracksPerAlbum, genre, baseUrl, onlySystemTracks, cancellationToken);

            if (!fillResult.Success)
            {
                _logger.LogWarning("Не удалось заполнить альбом {AlbumId}. Причина: {Reason}", album.Id, fillResult.ErrorMessage);
                await transaction.RollbackAsync(cancellationToken);
                return ServiceResult<List<AlbumDto>>.Fail($"Не удалось создать альбом: {fillResult.ErrorMessage}");
            }

            createdAlbums.Add(fillResult.Data!);
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await InvalidateAlbumsCacheAsync(cancellationToken);

        _logger.LogInformation("Сгенерировано {Count} альбомов", createdAlbums.Count);
        return ServiceResult<List<AlbumDto>>.Ok(createdAlbums);
    }

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
            var trimmedGenre = genre.Trim().ToLower();
            query = query.Where(t => t.Genre != null && t.Genre.ToLower() == trimmedGenre);
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

        var allAlbumTracks = await _context.Tracks
            .Where(t => t.AlbumId == albumId)
            .ToListAsync(cancellationToken);

        await InvalidateAlbumsCacheAsync(cancellationToken);
        await InvalidateTracksCacheAsync(randomTracks.Select(t => t.Id), cancellationToken);

        _logger.LogInformation("Album {AlbumId} automatically filled with {Count} random tracks. Genre filter: {Genre}, OnlySystemTracks: {OnlySystemTracks}",
            albumId, randomTracks.Count, genre ?? "None", onlySystemTracks);

        return ServiceResult<AlbumDto>.Ok(MapToDto(album, allAlbumTracks, baseUrl, isLiked: false));
    }

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