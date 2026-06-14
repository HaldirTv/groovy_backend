using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Grpc; // Подключаем пространство имен gRPC
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/[controller]")]
[Produces("application/json")]
public class UploadController : ControllerBase
{
    private readonly UploadService _uploadService;
    private readonly UserNameGrpcService.UserNameGrpcServiceClient _grpcClient; // Наш gRPC клиент
    private readonly ILogger<UploadController> _logger;

    public UploadController(
        UploadService uploadService,
        UserNameGrpcService.UserNameGrpcServiceClient grpcClient,
        ILogger<UploadController> logger)
    {
        _uploadService = uploadService;
        _grpcClient = grpcClient;
        _logger = logger;
    }

    [HttpPost("track")]
    [RequestSizeLimit(220_000_000)]          // 220 MB hard limit
    [RequestFormLimits(MultipartBodyLengthLimit = 220_000_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadTrackResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UploadTrack(
        [FromForm] UploadTrackRequestDto dto,
        CancellationToken cancellationToken)
    {
        // ── 1. Валидация базовой модели ─────────────────────────────────────
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (string.IsNullOrWhiteSpace(dto.Title))
            return BadRequest(new { Error = "Title is required." });

        if (dto.File is null || dto.File.Length == 0)
            return BadRequest(new { Error = "An audio file must be provided." });

        // ── 2. Читаем заголовки шлюза (кто делает запрос) ──────────────────
        // var userIdString = Request.Headers["X-User-Id"].ToString();
        // var userRole = Request.Headers["X-User-Role"].ToString();

        // if (!Guid.TryParse(userIdString, out Guid currentUserId))
        // {
        //     return Unauthorized(new { Error = "User ID missing or invalid in Gateway headers." });
        // }

        // ── 3. Проверка общих прав ──────────────────────────────────────────
        // if (userRole != "Artist" && userRole != "Admin")
        // {
        //     return StatusCode(StatusCodes.Status403Forbidden, new { Error = "Only Artists or Admins can upload tracks." });
        // }

        // Временно мокаем пользователя для тестирования без авторизации
        Guid currentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid finalOwnerId = currentUserId;
        string finalArtistName = "Test Artist";

        // ── 4. Исправленная логика gRPC и определения владельца ─────────────
        /*
        // Сценарий А: Запрос делает Админ И он указал, для какого артиста загружает трек
        if (userRole == "Admin" && dto.TargetUserId.HasValue)
        {
            finalOwnerId = dto.TargetUserId.Value; // Трек запишется на этого артиста!

            try
            {
                // Идем по gRPC в Auth, чтобы проверить, существует ли такой артист и взять его имя
                var grpcRequest = new UserNameGrpcRequest { UserId = finalOwnerId.ToString() };
                var grpcResponse = await _grpcClient.GetUserNameGrpcAsync(grpcRequest, cancellationToken: cancellationToken);

                finalArtistName = grpcResponse.Username; // Взяли реальное имя из базы Auth
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                return BadRequest(new { Error = $"Target artist with ID {finalOwnerId} not found in Auth database." });
            }
            catch (Grpc.Core.RpcException ex)
            {
                _logger.LogError(ex, "gRPC call failed while Admin was uploading track.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Auth service unavailable." });
            }
        }
        // Сценарий Б: Обычный артист грузит сам себе (или админ грузит лично свой трек)
        else
        {
            finalOwnerId = currentUserId; // Владелец — тот, кто нажал кнопку

            try
            {
                // Запрашиваем имя текущего пользователя
                var grpcRequest = new UserNameGrpcRequest { UserId = userIdString };
                var grpcResponse = await _grpcClient.GetUserNameGrpcAsync(grpcRequest, cancellationToken: cancellationToken);

                finalArtistName = grpcResponse.Username;
            }
            catch (Grpc.Core.RpcException ex)
            {
                _logger.LogError(ex, "gRPC call failed for current user.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Auth service unavailable." });
            }
        }
        */


        try
        {
            // Передаем currentUserId и finalArtistName в сервис!
            var track = await _uploadService.UploadTrackAsync(dto, finalOwnerId, finalArtistName, cancellationToken);
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            // AudioUrl указывает на streaming endpoint с поддержкой HTTP Range
            var audioUrl = $"{baseUrl}/music/tracks/{track.Id}/stream";
            var coverUrl = track.CoverImageRelativePath is not null
                ? $"{baseUrl}/music/files/covers/{track.Id}_cover{System.IO.Path.GetExtension(track.CoverImageRelativePath)}"
                : null;

            var response = new UploadTrackResponseDto
            {
                TrackId = track.Id,
                Title = track.Title,
                ArtistName = track.ArtistName, // Вернется уже проверенное имя
                Album = track.Album,
                Genre = track.Genre,
                DurationSeconds = track.DurationSeconds,
                FileSizeBytes = track.FileSizeBytes,
                ContentType = track.ContentType,
                AudioUrl = audioUrl,
                CoverImageUrl = coverUrl,
                UploadedAt = track.UploadedAt,
            };

            _logger.LogInformation("Track uploaded successfully. Id={TrackId}, OwnerId={OwnerId}, UploadedBy={UploaderId}",
                track.Id, finalOwnerId, currentUserId);

            return CreatedAtAction(nameof(UploadTrack), new { id = track.Id }, response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Upload validation failed: {Message}", ex.Message);
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during track upload.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "An unexpected error occurred." });
        }
    }
}