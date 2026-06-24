using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

/// <summary>
/// Управление треками: получение списка, удаление, переименование.
/// Base route: /music/tracks
/// </summary>
[ApiController]
[Route("music/[controller]")]
[Produces("application/json")]
public class TracksController : ControllerBase
{
    private readonly MusicService _musicService;
    private readonly ILogger<TracksController> _logger;

    public TracksController(MusicService musicService, ILogger<TracksController> logger)
    {
        _musicService = musicService;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<TrackDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllTracks(
        [FromQuery] string? search,
        [FromQuery] Guid? userId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        // Защита от дурака: не даем передать отрицательные или нулевые значения
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100; // Ограничиваем максимум, чтобы базу не положили

        // Получаем порцию данных и общее количество из сервиса
        var (tracks, totalCount) = await _musicService.GetAllTracksAsync(search, userId, pageNumber, pageSize, cancellationToken);
        
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        // Маппим треки в DTO
        var trackDtos = tracks.Select(t => MapToDto(t, baseUrl)).ToList();

        // Заворачиваем в красивый постраничный ответ
        var result = new PagedResultDto<TrackDto>(trackDtos, totalCount, pageNumber, pageSize);

        return Ok(result);
    }


    // ─── GET /music/tracks/{id} ───────────────────────────────────────────────

    /// <summary>
    /// GET /music/tracks/{id}
    /// Получает полную информацию о треке по его ID (включая готовые ссылки на прослушивание).
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TrackDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTrackById(Guid id, CancellationToken cancellationToken)
    {
        var track = await _musicService.GetTrackByIdAsync(id, cancellationToken);

        if (track is null)
            return NotFound(new { Error = $"Трек с id '{id}' не найден." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        // Отдаем красивый DTO, где уже лежат готовые URL для плеера
        return Ok(MapToDto(track, baseUrl));
    }

    // ─── GET /music/tracks/{id}/stream ───────────────────────────────────────

    /// <summary>
    /// Потоковая отдача аудиофайла с поддержкой HTTP Range Requests (RFC 7233).
    /// </summary>
    /// <remarks>
    /// Поддерживает частичный контент (HTTP 206) для перемотки и посекундной загрузки
    /// аудио в браузерных плеерах без полной загрузки файла.
    ///
    /// **Заголовок Range:**
    /// Клиент может передать заголовок `Range: bytes=0-99`, чтобы получить
    /// первые 100 байт файла. Сервер ответит `206 Partial Content` с заголовками
    /// `Content-Range` и `Accept-Ranges: bytes`.
    ///
    /// **Кэширование:**
    /// Ответ содержит `Cache-Control: public, max-age=31536000, immutable`,
    /// что позволяет браузеру и CDN кэшировать аудиофайл на 1 год.
    /// Аудиофайлы идентифицируются по неизменяемому GUID трека, поэтому
    /// долгосрочное кэширование безопасно.
    ///
    /// **Авторизация:**
    /// Требует заголовок `X-User-Id` (проставляется API Gateway после проверки JWT).
    /// </remarks>
    /// <param name="id">GUID трека.</param>
    /// <response code="200">Аудиофайл отдаётся целиком (без заголовка Range).</response>
    /// <response code="206">Частичный контент — ответ на запрос с заголовком <c>Range</c>.</response>
    /// <response code="401">Заголовок <c>X-User-Id</c> отсутствует или недействителен.</response>
    /// <response code="404">Трек или аудиофайл не найдены.</response>
    /// <response code="416">Запрошенный диапазон байт выходит за пределы файла.</response>
    [HttpGet("{id:guid}/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    public async Task<IActionResult> StreamTrack(Guid id, CancellationToken cancellationToken = default)
    {
        // 1. Проверка авторизации шлюза (оставляем твой код)
        var userIdString = Request.Headers["X-User-Id"].ToString();
        if (!Guid.TryParse(userIdString, out _))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        // 2. Достаем трек из базы, чтобы проверить, локальный он или внешний
        var track = await _musicService.GetTrackByIdAsync(id, cancellationToken);
        if (track is null)
            return NotFound(new { Error = "Трек не найден." });

        // ─── ФИШКА: Если трек из Jamendo ────────────────────────────────────────
        if (track.IsExternal)
        {
            // Атомарно увеличиваем счетчик прослушиваний в нашей БД (fire-and-forget)!
            await _musicService.IncrementPlayCountAsync(id, CancellationToken.None);

            _logger.LogInformation("Stream (External): редирект трека {TrackId} на Jamendo URL.", id);

            // Перенаправляем браузер/плеер фронтенда качать трек напрямую с серверов Jamendo
            return Redirect(track.ExternalAudioUrl!);
        }

        // ─── Логика для локальных файлов (твой старый рабочий код) ───────────────
        var fileInfo = await _musicService.GetTrackFileInfoAsync(id, cancellationToken);
        if (fileInfo is null) return NotFound();

        var (absolutePath, contentType) = fileInfo.Value;
        if (!System.IO.File.Exists(absolutePath)) return NotFound();

        await _musicService.IncrementPlayCountAsync(id, CancellationToken.None);
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        return PhysicalFile(absolutePath, contentType, enableRangeProcessing: true);
    }

    // ─── DELETE /music/tracks/{id} ────────────────────────────────────────────

    /// <summary>
    /// DELETE /music/tracks/{id}
    /// Удаляет трек и связанные медиафайлы с диска.
    /// </summary>
    /// <param name="id">GUID трека.</param>
    /// <response code="204">Трек успешно удалён.</response>
    /// <response code="404">Трек с указанным id не найден.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)] // Добавили документацию для 403 ошибки
    public async Task<IActionResult> DeleteTrack(Guid id, CancellationToken cancellationToken)
    {

        var userIdString = Request.Headers["X-User-Id"].ToString();
        var userRole = Request.Headers["X-User-Role"].ToString();

        if (!Guid.TryParse(userIdString, out Guid currentUserId))
            return Unauthorized(new { Error = "Invalid or missing user identity." });

        try
        {

            var result = await _musicService.DeleteTrackAsync(id, currentUserId, userRole, cancellationToken);


            if (!result)
            {
                return NotFound(new { Error = $"Track with ID {id} not found." });
            }


            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {

            return StatusCode(StatusCodes.Status403Forbidden, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while deleting track {TrackId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "An unexpected error occurred." });
        }
    }

    // ─── PATCH /music/tracks/{id}/title ──────────────────────────────────────

    /// <summary>
    /// PATCH /music/tracks/{id}/title
    /// Изменяет название трека.
    /// </summary>
    /// <param name="id">GUID трека.</param>
    /// <param name="dto">Новое название.</param>
    /// <response code="200">Трек успешно переименован, возвращает обновлённый объект.</response>
    /// <response code="400">Некорректное тело запроса.</response>
    /// <response code="404">Трек с указанным id не найден.</response>
    [HttpPatch("{id:guid}")] // Для переименования (частичного обновления) лучше всего подходит PATCH
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public async Task<IActionResult> RenameTrack(
    Guid id,
    [FromBody] RenameTrackRequestDto dto,
    CancellationToken cancellationToken)
{
    // 1. Валидация входного DTO
    if (!ModelState.IsValid)
        return ValidationProblem(ModelState);

    if (string.IsNullOrWhiteSpace(dto.Title))
        return BadRequest(new { Error = "New title cannot be empty." });

    // 2. Достаем данные юзера, которые шлюз (Gateway) бережно вытащил из JWT
    var userIdString = Request.Headers["X-User-Id"].ToString();
    var userRole = Request.Headers["X-User-Role"].ToString();

    if (!Guid.TryParse(userIdString, out Guid currentUserId))
    {
        return Unauthorized(new { Error = "User identity is missing or invalid." });
    }

    try
    {
        // 3. Передаем ВСЕ параметры в сервис (исправили двойную запятую и нехватку аргументов)
        var track = await _musicService.RenameTrackAsync(
            id,
            dto.Title,
            currentUserId,
            userRole,
            cancellationToken);

        // 4. Если сервис вернул null — значит трек не найден в БД
        if (track is null)
        {
            return NotFound(new { Error = $"Трек с id '{id}' не найден." });
        }

        // 5. Всё ок, маппим и отдаем обновленный трек
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(MapToDto(track, baseUrl));
    }
    catch (UnauthorizedAccessException ex)
    {
        // Если сервис понял, что трек чужой и юзер не админ — отдаем 403
        return StatusCode(StatusCodes.Status403Forbidden, new { Error = ex.Message });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unexpected error while renaming track {TrackId}", id);
        return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "An unexpected error occurred." });
    }
}

    // ─── helpers ────────────────────────────────────────────────────────

    private static TrackDto MapToDto(Track track, string baseUrl)
    {
        // Ссылка на стриминг всегда ведет на наш эндпоинт, 
        // потому что наш эндпоинт умеет делать редирект на Jamendo + крутить счетчик прослушиваний!
        var audioUrl = $"{baseUrl}/music/tracks/{track.Id}/stream";

        string? coverUrl = null;

        // ИСПРАВЛЕНИЕ: Если трек внешний — берем готовую ссылку на обложку Jamendo
        if (track.IsExternal)
        {
            coverUrl = track.ExternalCoverUrl;
        }
        // Если локальный — строим путь к нашему хранилищу файлов
        else if (track.CoverImageRelativePath is not null)
        {
            var coverExt = Path.GetExtension(track.CoverImageRelativePath);
            coverUrl = $"{baseUrl}/music/files/covers/{track.Id}_cover{coverExt}";
        }

        return new TrackDto
        {
            TrackId         = track.Id,
            Title           = track.Title,
            ArtistName      = track.ArtistName,
            Album           = track.Album,
            Genre           = track.Genre,
            DurationSeconds = track.DurationSeconds,
            FileSizeBytes   = track.FileSizeBytes,
            ContentType     = track.ContentType,
            AudioUrl        = audioUrl,       // Сюда пойдет ссылка на твой /stream (он редиректнет куда надо)
            CoverImageUrl   = coverUrl,       // А сюда сразу пойдет готовая ссылка на картинку Jamendo
            UploadedAt      = track.UploadedAt,
            PlayCount       = track.PlayCount,
        };
    }
}
