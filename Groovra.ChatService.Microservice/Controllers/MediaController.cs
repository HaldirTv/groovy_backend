using Groovra.ChatService.Microservice.Services;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.ChatService.Microservice.Controllers;

[ApiController]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private const long MaxFileSizeBytes = 50L * 1024 * 1024;

    private readonly IFileStorageService _fileStorage;

    public MediaController(IFileStorageService fileStorage)
    {
        _fileStorage = fileStorage;
    }

    // POST /api/media/upload — завантажує довільний файл (картинка/голосове/файл) у
    // Cloudflare R2 і повертає публічний URL. Сам чат-меседж із цим URL надсилається
    // окремим запитом (POST /chat/conversations/{id}/messages/media) — два кроки,
    // а не один, щоб фронтенд міг показати прогрес завантаження до вибору чату.
    [HttpPost("upload")]
    [RequestSizeLimit(MaxFileSizeBytes)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out _))
            return Unauthorized(new { message = "Потрібна авторизація." });

        if (file == null || file.Length == 0)
            return BadRequest(new { message = "Файл не надано." });

        if (file.Length > MaxFileSizeBytes)
            return BadRequest(new { message = "Файл завеликий (максимум 50 МБ)." });

        await using var stream = file.OpenReadStream();
        var url = await _fileStorage.UploadFileAsync(stream, file.FileName, file.ContentType, cancellationToken);

        return Ok(new { url, fileName = file.FileName, fileSizeBytes = file.Length });
    }
}
