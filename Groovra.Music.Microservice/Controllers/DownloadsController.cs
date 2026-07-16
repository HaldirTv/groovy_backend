using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/downloads")]
[Produces("application/json")]
public class DownloadsController : ControllerBase
{
    private readonly DownloadService _downloadService;

    public DownloadsController(DownloadService downloadService)
    {
        _downloadService = downloadService;
    }

    [HttpGet]
    public async Task<IActionResult> GetDownloads([FromQuery] string? type, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Потрібна авторизація." });

        DownloadType? filterType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<DownloadType>(type, true, out var parsed))
                return BadRequest(new { Error = "Невірний тип завантаження." });
            filterType = parsed;
        }

        var items = await _downloadService.GetDownloadsAsync(userId, GetBaseUrl(), filterType, cancellationToken);
        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> AddDownload([FromBody] AddDownloadDto dto, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Потрібна авторизація." });

        var (success, error) = await _downloadService.AddDownloadAsync(userId, dto, cancellationToken);
        if (!success)
            return BadRequest(new { Message = error });

        return Ok(new { Message = "Додано до завантажень." });
    }

    [HttpDelete]
    public async Task<IActionResult> RemoveDownload(
        [FromQuery] string type,
        [FromQuery] Guid? itemId,
        [FromQuery] string? albumName,
        [FromQuery] string? artistName,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Потрібна авторизація." });

        if (!Enum.TryParse<DownloadType>(type, true, out var parsedType))
            return BadRequest(new { Error = "Невірний тип завантаження." });

        var result = await _downloadService.RemoveDownloadAsync(userId, parsedType, itemId, albumName, artistName, cancellationToken);
        if (!result)
            return NotFound(new { Message = "Запис не знайдено у завантаженнях." });

        return Ok(new { Message = "Видалено із завантажень." });
    }
    private bool TryGetUserId(out Guid userId) => HttpContext.TryGetUserId(out userId);
    private string GetBaseUrl() => $"{Request.Scheme}://{Request.Host}";
}