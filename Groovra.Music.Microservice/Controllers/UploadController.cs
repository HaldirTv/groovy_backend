using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Extensions;
using Groovra.Shared.Grpc; 
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/[controller]")]
[Produces("application/json")]
public class UploadController : ControllerBase
{
    private readonly UploadService _uploadService;
    private readonly UserNameGrpcService.UserNameGrpcServiceClient _grpcClient; 
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
    [RequestSizeLimit(220_000_000)]          
    [RequestFormLimits(MultipartBodyLengthLimit = 220_000_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadTrackResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UploadTrack(
        [FromForm] UploadTrackRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (string.IsNullOrWhiteSpace(dto.Title))
            return BadRequest(new { Error = "Title is required." });

        if (dto.File is null || dto.File.Length == 0)
            return BadRequest(new { Error = "An audio file must be provided." });

        var userIdString = Request.Headers["X-User-Id"].ToString();
        var userRole = Request.Headers["X-User-Role"].ToString();
        var userName = HttpContext.GetUserName();
        if (!Guid.TryParse(userIdString, out Guid currentUserId))
        {
            return Unauthorized(new { Error = "User ID missing or invalid in Gateway headers." });
        }

        if (userRole.Contains("Artist") == false && userRole.Contains("Admin") == false)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Error = "Only Artists or Admins can upload tracks." });
        }

        Guid finalOwnerId;
        string finalArtistName;

        if (userRole.Contains("Admin") == true && dto.TargetUserId.HasValue)
        {
            finalOwnerId = dto.TargetUserId.Value; 

            try
            {
                var grpcRequest = new UserNameGrpcRequest { UserId = finalOwnerId.ToString() };
                var grpcResponse = await _grpcClient.GetUserNameGrpcAsync(grpcRequest, cancellationToken: cancellationToken);

                finalArtistName = grpcResponse.Username; 
            }
            catch (global::Grpc.Core.RpcException ex) when (ex.StatusCode == global::Grpc.Core.StatusCode.NotFound)
            {
                return BadRequest(new { Error = $"Target artist with ID {finalOwnerId} not found in Auth database." });
            }
            catch (global::Grpc.Core.RpcException ex)
            {
                _logger.LogError(ex, "gRPC call failed while Admin was uploading track.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "Auth service unavailable." });
            }
        }
        else
        {
            finalOwnerId = currentUserId; 
            finalArtistName = !string.IsNullOrWhiteSpace(dto.ArtistName) ? dto.ArtistName : userName;
        }

        try
        {
            var track = await _uploadService.UploadTrackAsync(dto, finalOwnerId, finalArtistName, cancellationToken);
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var audioUrl = $"{baseUrl}/music/tracks/{track.Id}/stream";
            var coverUrl = track.CoverImageRelativePath is not null
                ? $"{baseUrl}/music/files/covers/{track.Id}_cover{System.IO.Path.GetExtension(track.CoverImageRelativePath)}"
                : null;

            var response = new UploadTrackResponseDto
            {
                TrackId = track.Id,
                Title = track.Title,
                ArtistName = track.ArtistName, 
                Album = track.AlbumTitle ?? track.Album?.Title,
                Genre = track.Genre,
                Mood = track.Mood,
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