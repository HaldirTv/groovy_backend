using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Result;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Groovra.Shared.Grpc;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/albums")]
public class AlbumsController : ControllerBase
{
    private readonly AlbumService _albumService;
    private readonly FavoritesService _favoritesService;
    private readonly UserNameGrpcService.UserNameGrpcServiceClient _grpcClient;
    private readonly ICacheService _cache;
    private readonly ILogger<AlbumsController> _logger;

    public AlbumsController(
        AlbumService albumService, FavoritesService favoritesService,
        UserNameGrpcService.UserNameGrpcServiceClient grpcClient, ICacheService cache,
        ILogger<AlbumsController> logger)
    {
        _albumService = albumService;
        _favoritesService = favoritesService;
        _grpcClient = grpcClient;
        _cache = cache;
        _logger = logger;
    }
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<AlbumListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllAlbums(
        [FromQuery] string? search,
        [FromQuery] Guid? userId,
        [FromQuery] string? genre,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        HttpContext.TryGetUserId(out var currentUserId);

        var likedIds = currentUserId != Guid.Empty
            ? await _favoritesService.GetLikedAlbumIdsAsync(currentUserId, cancellationToken)
            : new HashSet<Guid>();

        var (items, totalCount) = await GetAlbumsWithCacheAsync(
            userId, search, genre, pageNumber, pageSize, likedIds, GetBaseUrl(), cancellationToken);

        var result = new PagedResultDto<AlbumListItemDto>(items, totalCount, pageNumber, pageSize);
        return Ok(result);
    }
    [HttpGet("me")]
    public async Task<IActionResult> GetMyAlbums(
        [FromQuery] string? search,
        [FromQuery] string? genre,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();
        if (!userRole.HasRole(AppRoles.Artist) && !userRole.HasRole(AppRoles.Admin))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Тільки артисти мають власні альбоми." });

        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var likedIds = await _favoritesService.GetLikedAlbumIdsAsync(currentUserId, cancellationToken);

        var (items, totalCount) = await GetAlbumsWithCacheAsync(
            currentUserId, search, genre, pageNumber, pageSize, likedIds, GetBaseUrl(), cancellationToken);

        return Ok(new PagedResultDto<AlbumListItemDto>(items, totalCount, pageNumber, pageSize));
    }
    [HttpPost("{id:guid}/tracks")]
    public async Task<IActionResult> AddTracks(
        Guid id,
        [FromBody] AddTracksToAlbumDto dto,
        CancellationToken cancellationToken)
    {
        if (dto.TrackIds == null || !dto.TrackIds.Any())
            return BadRequest(new { message = "Список треків не може бути порожнім." });

        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var album = await _albumService.GetRawAlbumAsync(id, cancellationToken);
        if (album is null) return NotFound(new { message = "Альбом не знайдено." });

        var userRole = Request.Headers["X-User-Role"].ToString();
        bool isAdmin = userRole.HasRole(AppRoles.Admin);

        if (album.UserId != currentUserId && !isAdmin)
            return Forbid();

        var result = await _albumService.AddTracksToAlbumAsync(
            id, dto.TrackIds, cancellationToken);

        if (result.IsAlbumNotFound) 
            return NotFound(new { message = "Альбом не знайдено." });

        return Ok(new 
        { 
            message = $"Успішно додано треків: {result.AddedIds.Count}.",
            details = result 
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAlbum(Guid id, CancellationToken cancellationToken)
    {
        HttpContext.TryGetUserId(out var currentUserId);

        var isLiked = currentUserId != Guid.Empty &&
            (await _favoritesService.GetLikedAlbumIdsAsync(currentUserId, cancellationToken)).Contains(id);

        var result = await _albumService.GetAlbumByIdAsync(id, GetBaseUrl(), isLiked, cancellationToken);

        if (!result.Success)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

    [HttpPost]
    [Consumes("multipart/form-data")] 
    public async Task<IActionResult> CreateAlbum(
        [FromForm] CreateAlbumDto dto, 
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();
        bool isAdmin = userRole.HasRole(AppRoles.Admin);

        if (!userRole.HasRole(AppRoles.Artist) && !isAdmin)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Тільки артисти або адміни можуть створювати альбоми." });

        Guid ownerUserId = currentUserId;
        // X-User-Name несе системний логін (JWT кладе в ClaimTypes.Name саме Username), а
        // автором альбому має бути публічний DisplayName - те саме значення, яке потім
        // проставляє MusicUserNicknameChangedConsumer при зміні ніку. Тому тягнемо ім'я
        // з Auth по gRPC; хедер лишається фолбеком, щоб створення альбому не падало,
        // якщо Auth тимчасово недоступний.
        string artistName = HttpContext.GetUserName();

        if (isAdmin && dto.TargetUserId.HasValue)
        {
            ownerUserId = dto.TargetUserId.Value;
            try
            {
                var grpcRequest = new UserNameGrpcRequest { UserId = ownerUserId.ToString() };
                var grpcResponse = await _grpcClient.GetUserNameGrpcAsync(grpcRequest, cancellationToken: cancellationToken);
                artistName = grpcResponse.DisplayName;
            }
            catch (global::Grpc.Core.RpcException ex) when (ex.StatusCode == global::Grpc.Core.StatusCode.NotFound)
            {
                return BadRequest(new { Error = $"Target artist with ID {ownerUserId} not found in Auth database." });
            }
        }
        else
        {
            try
            {
                var grpcRequest = new UserNameGrpcRequest { UserId = ownerUserId.ToString() };
                var grpcResponse = await _grpcClient.GetUserNameGrpcAsync(grpcRequest, cancellationToken: cancellationToken);
                if (!string.IsNullOrWhiteSpace(grpcResponse.DisplayName))
                    artistName = grpcResponse.DisplayName;
            }
            catch (global::Grpc.Core.RpcException ex)
            {
                _logger.LogWarning(ex, "Не вдалося отримати DisplayName для {UserId}, використовуємо ім'я з хедера.", ownerUserId);
            }
        }

        var result = await _albumService.CreateAlbumAsync(
            ownerUserId, artistName, dto, GetBaseUrl(), cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return CreatedAtAction(nameof(GetAlbum), new { id = result.Data.Album.Id }, new 
        {
            Album = result.Data.Album,
            TrackImportDetails = result.Data.TrackDetails 
        });
    }

    [HttpPatch("{id:guid}")]
    [Consumes("multipart/form-data")] 
    public async Task<IActionResult> UpdateAlbum(
        Guid id,
        [FromForm] UpdateAlbumDto dto, 
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var album = await _albumService.GetRawAlbumAsync(id, cancellationToken);
        if (album is null) return NotFound(new { message = "Альбом не знайдено." });

        var userRole = Request.Headers["X-User-Role"].ToString();
        bool isAdmin = userRole.HasRole(AppRoles.Admin);

        if (album.UserId != currentUserId && !isAdmin)
            return Forbid();

        var result = await _albumService.UpdateAlbumAsync(id, currentUserId, dto, GetBaseUrl(), cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Альбом успішно оновлено." });
    }

    [HttpGet("deleted")]
    public async Task<IActionResult> GetDeletedAlbums(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var items = await _albumService.GetDeletedAlbumsAsync(currentUserId, GetBaseUrl(), cancellationToken);
        return Ok(items);
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> RestoreAlbum(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();

        var result = await _albumService.RestoreAlbumAsync(id, currentUserId, userRole, cancellationToken);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Альбом успішно відновлено." });
    }
    // DELETE music/albums/{id}/permanent — остаточне видалення вже soft-deleted
    // альбому (з кошика), не дожидаючись фонової очистки GarbageCollectorService.
    [HttpDelete("{id:guid}/permanent")]
    public async Task<IActionResult> PermanentlyDeleteAlbum(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();

        var result = await _albumService.PermanentlyDeleteAlbumAsync(id, currentUserId, userRole, cancellationToken);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Альбом остаточно видалено." });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAlbum(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var album = await _albumService.GetRawAlbumAsync(id, cancellationToken);
        if (album is null) return NotFound(new { message = "Альбом не знайдено." });

        var userRole = Request.Headers["X-User-Role"].ToString();
        if (album.UserId != currentUserId && !userRole.HasRole(AppRoles.Admin))
            return Forbid();

        var result = await _albumService.DeleteAlbumAsync(id, cancellationToken);
        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(new { message = "Альбом видалено." });
    }

    [HttpDelete("{id:guid}/tracks/{trackId:guid}")]
    public async Task<IActionResult> RemoveTrack(
        Guid id,
        Guid trackId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var album = await _albumService.GetRawAlbumAsync(id, cancellationToken);
        if (album is null) return NotFound(new { message = "Альбом не знайдено." });

        var userRole = Request.Headers["X-User-Role"].ToString();
        if (album.UserId != currentUserId && !userRole.HasRole(AppRoles.Admin))
            return Forbid();

        var result = await _albumService.RemoveTrackFromAlbumAsync(id, trackId, cancellationToken);

        if (!result.Success)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(new { message = "Трек видалено з альбому." });
    }

    [HttpPost("generate-random")]
    public async Task<IActionResult> GenerateRandomAlbums(
        [FromQuery] int albumsCount = 3,       
        [FromQuery] int tracksPerAlbum = 5,    
        [FromQuery] string? genre = null,      
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (albumsCount <= 0 || tracksPerAlbum <= 0)
            {
                return BadRequest(new { Error = "Количество альбомов и треков должно быть больше 0." });
            }

            if (!TryGetUserId(out var currentUserId))
            {
                return Unauthorized(new { Error = "Потребна авторизація (Токен не валидный или отсутствует)." });
            }

            var userRole = Request.Headers["X-User-Role"].ToString();
            if (!userRole.HasRole(AppRoles.Admin))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Error = "Недостаточно прав для генерации тестовых данных." });
            }

            var systemUserId = Guid.Parse("00000000-0000-0000-0000-000000000000");
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            string artistName = "Groovra System Bot"; 

            var result = await _albumService.GenerateRandomAlbumsAsync(
                systemUserId, 
                artistName, 
                albumsCount, 
                tracksPerAlbum, 
                genre, 
                baseUrl, 
                true,
                cancellationToken);

            if (!result.Success)
            {
                return BadRequest(new 
                { 
                    Error = "Не удалось автоматически заполнить альбомы.",
                    Reason = result.ErrorMessage 
                });
            }

            return Ok(new 
            { 
                Message = $"Альбомы успешно сгенерированы ({result.Data.Count} шт.) и привязаны к System Action.",
                Count = result.Data.Count,
                Albums = result.Data 
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new 
            { 
                Error = "Внутренняя ошибка сервера при генерации случайных альбомов.",
                Details = ex.Message 
            });
        }
    }

    private bool TryGetUserId(out Guid userId) => HttpContext.TryGetUserId(out userId);
    private string GetBaseUrl() => $"{Request.Scheme}://{Request.Host}";

    /// <summary>Cache-aside для GetAllAlbums/GetMyAlbums. Кеш будується БЕЗ likedAlbumIds
    /// (щоб не запекти лайки одного користувача в спільний кеш), IsLiked накладається на
    /// результат — з кеша чи щойно з БД — уже після читання, для поточного запиту.</summary>
    private async Task<(IReadOnlyList<AlbumListItemDto> Items, int TotalCount)> GetAlbumsWithCacheAsync(
        Guid? artistUserId, string? search, string? genre, int pageNumber, int pageSize,
        HashSet<Guid> likedAlbumIds, string baseUrl, CancellationToken cancellationToken)
    {
        var key = CacheKeys.AlbumsSearch(search, artistUserId, genre, pageNumber, pageSize);
        var cached = await _cache.GetAsync<CachedPage<AlbumListItemDto>>(key, cancellationToken);

        IReadOnlyList<AlbumListItemDto> items;
        int totalCount;
        if (cached is not null)
        {
            items = cached.Items;
            totalCount = cached.TotalCount;
        }
        else
        {
            (items, totalCount) = await _albumService.GetAlbumsAsync(
                artistUserId, search, likedAlbumIds: [], baseUrl, genre, pageNumber, pageSize, cancellationToken);
            await _cache.SetAsync(
                key, new CachedPage<AlbumListItemDto> { Items = items.ToList(), TotalCount = totalCount },
                TimeSpan.FromMinutes(5), cancellationToken);
        }

        foreach (var item in items)
            item.IsLiked = likedAlbumIds.Contains(item.Id);

        return (items, totalCount);
    }
}