using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Controllers;

// Свідомо БЕЗ [Authorize]: Music-сервіс не реєструє authentication scheme - JWT перевіряє
// gateway і прокидає X-User-Id / X-User-Role заголовками. [Authorize] без default scheme
// кидав би виняток на кожному запиті. Захист: policy "AdminOnly" на маршруті gateway
// + EnsureAdmin() нижче (той самий патерн, що й в усіх інших контролерах сервісу).
[ApiController]
[Route("music/admin")]
public class AdminTracksController : ControllerBase
{
    private readonly MusicDbContext _db;
    private readonly MusicService _musicService;
    private readonly ILogger<AdminTracksController> _logger;

    public AdminTracksController(MusicDbContext db, MusicService musicService, ILogger<AdminTracksController> logger)
    {
        _db = db;
        _musicService = musicService;
        _logger = logger;
    }

    private bool EnsureAdmin(out IActionResult? forbidResult)
    {
        if (!HttpContext.UserIsInRole(AppRoles.Admin))
        {
            forbidResult = StatusCode(StatusCodes.Status403Forbidden, new { Message = "Недостатньо прав." });
            return false;
        }
        forbidResult = null;
        return true;
    }

    [HttpGet("tracks")]
    public async Task<IActionResult> GetAllTracks(
        [FromQuery] string? search,
        [FromQuery] string? genre,
        [FromQuery] string? status,
        [FromQuery] bool? includeDeleted = false,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ctoken = default)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 50;

        // На Tracks висить глобальний фільтр (!IsDeleted), тому щоб адмін реально побачив
        // кошик, потрібен IgnoreQueryFilters. У вихідній адмін-гілці обидві гілки тернарника
        // були однакові, через що прапорець нічого не робив.
        var query = (includeDeleted ?? false)
            ? _db.Tracks.AsNoTracking().IgnoreQueryFilters().AsQueryable()
            : _db.Tracks.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(t =>
                t.Title.ToLower().Contains(s) ||
                t.ArtistName.ToLower().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(genre) && genre != "all")
        {
            query = query.Where(t => t.Genre == genre);
        }

        if (status == "flagged")
        {
            query = query.Where(t => t.PlayCount < 10);
        }
        else if (status == "active")
        {
            query = query.Where(t => t.PlayCount >= 10);
        }

        var totalCount = await query.CountAsync(ctoken);

        var items = await query
            .OrderByDescending(t => t.PlayCount)
            .ThenByDescending(t => t.UploadedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.ArtistName,
                t.Genre,
                t.PlayCount,
                t.IsAIGenerated,
                t.UploadedAt,
                t.IsDeleted,
                t.DeletedAt,
                t.CoverImageRelativePath,
                t.IsExternal,
                t.ExternalCoverUrl
            })
            .ToListAsync(ctoken);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var dtoItems = items.Select(t => new AdminTrackDto
        {
            Id = t.Id.ToString(),
            Code = $"GRV-{t.Id.ToString("N")[..4]}{t.Id.ToString("N")[4..8]}",
            Title = t.Title,
            Artist = t.ArtistName,
            Genre = t.Genre ?? "Без жанру",
            Plays = t.PlayCount.ToString(),
            PlaysValue = t.PlayCount,
            Status = t.PlayCount >= 10 ? "active" : "flagged",
            AiGen = t.IsAIGenerated,
            CreatedAt = t.UploadedAt,
            IsDeleted = t.IsDeleted,
            CoverUrl = t.IsExternal
                ? t.ExternalCoverUrl
                : !string.IsNullOrWhiteSpace(t.CoverImageRelativePath)
                    ? $"{baseUrl}/music/files/{t.CoverImageRelativePath.Replace('\\', '/')}"
                    : null
        }).ToList();

        return Ok(new
        {
            Items = dtoItems,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        });
    }

    [HttpGet("genres")]
    public async Task<IActionResult> GetGenres(CancellationToken ctoken = default)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;

        var genres = await _db.Tracks
            .AsNoTracking()
            .Where(t => !string.IsNullOrWhiteSpace(t.Genre))
            .Select(t => t.Genre)
            .Distinct()
            .OrderBy(g => g)
            .ToListAsync(ctoken);

        return Ok(genres);
    }

    /// <summary>Переносить трек у кошик. Делегує в MusicService.DeleteTrackAsync замість
    /// власного IsDeleted = true: там уже є транзакція, перерахунок лічильників альбому й
    /// плейлистів, публікація TrackDeletedEvent (History чистить історію прослуховувань)
    /// та інвалідація Redis. Пряме проставляння прапорця лишало б стухлий кеш і зламані
    /// лічильники. Семантику кошика не змінюємо: зв'язки альбом-трек зберігаються, звільняє
    /// їх лише остаточне видалення.</summary>
    [HttpDelete("tracks/{trackId:guid}")]
    public async Task<IActionResult> DeleteTrack(Guid trackId, CancellationToken ctoken = default)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        if (!HttpContext.TryGetUserId(out var adminUserId))
            return Unauthorized(new { Message = "Потрібна авторизація." });

        var userRoles = Request.Headers["X-User-Role"].ToString();

        try
        {
            var deleted = await _musicService.DeleteTrackAsync(trackId, adminUserId, userRoles, ctoken);
            if (!deleted)
                return NotFound(new { Message = "Треку не знайдено." });

            return Ok(new { Message = "Трек розміщено в кошик." });
        }
        catch (UnauthorizedAccessException ex)
        {
            // Той самий патерн, що й у TracksController.DeleteTrack: MusicService кидає це,
            // якщо викликач не власник і не адмін. Сюди адмін не мав би дійти (EnsureAdmin уже
            // це перевірив), але захист в один рівень краще, ніж необроблений 500 назовні.
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = ex.Message });
        }
    }

    [HttpPatch("tracks/{trackId:guid}/status")]
    public async Task<IActionResult> UpdateTrackStatus(
        Guid trackId,
        [FromBody] UpdateTrackStatusRequestDto dto,
        CancellationToken ctoken = default)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;

        var track = await _db.Tracks.Where(t => t.Id == trackId).FirstOrDefaultAsync(ctoken);
        if (track == null)
            return NotFound(new { Message = "Треку не знайдено." });

        switch (dto.Status.ToLower())
        {
            case "active":
                // Set a minimum play count so the frontend considers it "active"
                if (track.PlayCount < 10) track.PlayCount = 10;
                break;
            case "flagged":
                track.PlayCount = 0;
                break;
            default:
                return BadRequest(new { Message = "Невідомий статус. Дозволено: active, flagged." });
        }

        await _db.SaveChangesAsync(ctoken);
        return Ok(new { Message = "Статус оновлено." });
    }

    /// <summary>Масовий перенос у кошик. Так само делегує в MusicService по кожному треку,
    /// щоб кеш, лічильники й події лишались консистентними.</summary>
    [HttpPost("tracks/bulk-delete")]
    public async Task<IActionResult> BulkDeleteTracks(
        [FromBody] BulkDeleteRequestDto dto,
        CancellationToken ctoken = default)
    {
        if (!EnsureAdmin(out var forbid)) return forbid!;
        if (!HttpContext.TryGetUserId(out var adminUserId))
            return Unauthorized(new { Message = "Потрібна авторизація." });

        if (dto.TrackIds == null || dto.TrackIds.Count == 0)
            return BadRequest(new { Message = "Не вказано жодного ID." });

        var userRoles = Request.Headers["X-User-Role"].ToString();

        var count = 0;
        foreach (var rawId in dto.TrackIds.Distinct())
        {
            if (!Guid.TryParse(rawId, out var trackId))
                continue;

            try
            {
                if (await _musicService.DeleteTrackAsync(trackId, adminUserId, userRoles, ctoken))
                    count++;
            }
            catch (Exception ex)
            {
                // Один проблемний трек не має зривати всю пачку.
                _logger.LogError(ex, "Bulk-delete: не вдалося видалити трек {TrackId}", trackId);
            }
        }

        return Ok(new { Message = $"Розміщено {count} треків у кошик." });
    }

    public class AdminTrackDto
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string Plays { get; set; } = string.Empty;
        public long PlaysValue { get; set; }
        public string Status { get; set; } = "active";
        public bool AiGen { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsDeleted { get; set; }
        public string? CoverUrl { get; set; }
    }

    public class UpdateTrackStatusRequestDto
    {
        public string Status { get; set; } = string.Empty;
    }

    public class BulkDeleteRequestDto
    {
        public List<string> TrackIds { get; set; } = new();
    }
}
