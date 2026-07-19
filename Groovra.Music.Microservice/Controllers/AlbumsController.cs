using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Result;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Groovra.Shared.Grpc;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

/// <summary>
/// Управление альбомами: создание, получение, редактирование, привязка/отвязка треков.
/// Base route: /music/albums
/// </summary>
[ApiController]
[Route("music/albums")]
public class AlbumsController : ControllerBase
{
    private readonly AlbumService _albumService;
    private readonly FavoritesService _favoritesService;
    private readonly UserNameGrpcService.UserNameGrpcServiceClient _grpcClient;

    public AlbumsController(AlbumService albumService, FavoritesService favoritesService, UserNameGrpcService.UserNameGrpcServiceClient grpcClient)
    {
        _albumService = albumService;
        _favoritesService = favoritesService;
        _grpcClient = grpcClient;
    }
    // GET music/albums
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<AlbumListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllAlbums(
        [FromQuery] string? search,
        [FromQuery] Guid? userId,
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

        // 4. Отримуємо список альбомів із сервісу
        // Використовуємо готовий приватний метод GetBaseUrl() з AlbumsController
        var (items, totalCount) = await _albumService.GetAlbumsAsync(
            userId, 
            search, 
            likedIds, 
            GetBaseUrl(), 
            pageNumber, 
            pageSize, 
            cancellationToken);

        // 5. Формуємо результат
        var result = new PagedResultDto<AlbumListItemDto>(items, totalCount, pageNumber, pageSize);
        return Ok(result);
    }
    
    // GET music/albums/me
    [HttpGet("me")]
    public async Task<IActionResult> GetMyAlbums(
        [FromQuery] string? search,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // 1. Проверяем авторизацию
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        // 2. Свои альбомы могут быть только у артистов
        var userRole = Request.Headers["X-User-Role"].ToString();
        if (!userRole.HasRole(AppRoles.Artist) && !userRole.HasRole(AppRoles.Admin))
            return StatusCode(StatusCodes.Status403Forbidden, 
                new { message = "Тільки артисти мають власні альбоми." });

        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        // 3. Достаем лайки (чтобы на фронте сердечки горели, если артист лайкнул свой же альбом)
        var likedIds = await _favoritesService.GetLikedAlbumIdsAsync(currentUserId, cancellationToken);

        // 4. Вызываем твой готовый сервис, жестко передавая currentUserId в качестве фильтра
        var (items, totalCount) = await _albumService.GetAlbumsAsync(
            currentUserId, search, likedIds, GetBaseUrl(), pageNumber, pageSize, cancellationToken);

        return Ok(new PagedResultDto<AlbumListItemDto>(items, totalCount, pageNumber, pageSize));
    }
    
    //POST music/albums/{id}/tracks
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
            details = result // Твій BulkTrackOperationResult ідеально лягає сюди!
        });
    }

    
    
    // GET music/albums/{id}
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

    
    
    // POST music/albums
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
        string artistName = HttpContext.GetUserName();

        // Логіка gRPC для адмінів...
        if (isAdmin && dto.TargetUserId.HasValue)
        {
            ownerUserId = dto.TargetUserId.Value;
            try
            {
                var grpcRequest = new UserNameGrpcRequest { UserId = ownerUserId.ToString() };
                var grpcResponse = await _grpcClient.GetUserNameGrpcAsync(grpcRequest, cancellationToken: cancellationToken);
                artistName = grpcResponse.Username; 
            }
            catch (global::Grpc.Core.RpcException ex) when (ex.StatusCode == global::Grpc.Core.StatusCode.NotFound)
            {
                return BadRequest(new { Error = $"Target artist with ID {ownerUserId} not found in Auth database." });
            }
        }

        // Викликаємо сервіс, який тепер повертає Tuple (AlbumDto, BulkTrackOperationResult)
        var result = await _albumService.CreateAlbumAsync(
            ownerUserId, artistName, dto, GetBaseUrl(), cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        // Формуємо круту відповідь: віддаємо і сам альбом, і деталі імпорту треків!
        return CreatedAtAction(nameof(GetAlbum), new { id = result.Data.Album.Id }, new 
        {
            Album = result.Data.Album,
            TrackImportDetails = result.Data.TrackDetails // Твій BulkTrackOperationResult тут!
        });
    }

    
    
    // PATCH music/albums/{id}
    [HttpPatch("{id:guid}")]
    [Consumes("multipart/form-data")] // Указываем, что ждем форму с файлами
    public async Task<IActionResult> UpdateAlbum(
        Guid id,
        [FromForm] UpdateAlbumDto dto, // Меняем FromBody на FromForm
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

    // GET music/albums/deleted
    [HttpGet("deleted")]
    public async Task<IActionResult> GetDeletedAlbums(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var items = await _albumService.GetDeletedAlbumsAsync(currentUserId, GetBaseUrl(), cancellationToken);
        return Ok(items);
    }

    
    
// POST music/albums/{id}/restore
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
    
    
    // DELETE music/albums/{id}
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

    
    
    
    // DELETE music/albums/{id}/tracks/{trackId}
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

    
    
    
    // POST music/albums/generate-random?albumsCount=3&tracksPerAlbum=6&genre=Pop
    [HttpPost("generate-random")]
    public async Task<IActionResult> GenerateRandomAlbums(
        [FromQuery] int albumsCount = 3,       // Сколько альбомов сгенерировать
        [FromQuery] int tracksPerAlbum = 5,    // Сколько треков в каждый альбом
        [FromQuery] string? genre = null,      // Опциональный жанр
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Валидация входных параметров
            if (albumsCount <= 0 || tracksPerAlbum <= 0)
            {
                return BadRequest(new { Error = "Количество альбомов и треков должно быть больше 0." });
            }

            // 2. Проверяем авторизацию юзера
            if (!TryGetUserId(out var currentUserId))
            {
                return Unauthorized(new { Error = "Потребна авторизація (Токен не валидный или отсутствует)." });
            }

            // 3. Проверяем права (допускаем только Админов)
            var userRole = Request.Headers["X-User-Role"].ToString();
            if (!userRole.HasRole(AppRoles.Admin))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Error = "Недостаточно прав для генерации тестовых данных." });
            }

            // 4. Инициализируем системные данные в твоем стиле
            var systemUserId = Guid.Parse("00000000-0000-0000-0000-000000000000");
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            string artistName = "Groovra System Bot"; 

            // 5. Вызываем метод сервиса
            var result = await _albumService.GenerateRandomAlbumsAsync(
                systemUserId, // Передаем системный GUID как владельца альбомов
                artistName, 
                albumsCount, 
                tracksPerAlbum, 
                genre, 
                baseUrl, 
                true,
                cancellationToken);

            // 6. Если сервис вернул ошибку (например, кончились свободные треки в БД)
            if (!result.Success)
            {
                return BadRequest(new 
                { 
                    Error = "Не удалось автоматически заполнить альбомы.",
                    Reason = result.ErrorMessage 
                });
            }

            // 7. Всё ок, возвращаем успешный статус и данные
            return Ok(new 
            { 
                Message = $"Альбомы успешно сгенерированы ({result.Data.Count} шт.) и привязаны к System Action.",
                Count = result.Data.Count,
                Albums = result.Data 
            });
        }
        catch (Exception ex)
        {
            // Если что-то наглухо упало внутри самого метода или БД
            return StatusCode(500, new 
            { 
                Error = "Внутренняя ошибка сервера при генерации случайных альбомов.",
                Details = ex.Message 
            });
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private bool TryGetUserId(out Guid userId) => HttpContext.TryGetUserId(out userId);
    private string GetBaseUrl() => $"{Request.Scheme}://{Request.Host}";
}