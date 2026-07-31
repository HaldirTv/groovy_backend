using System.Net.Http.Json;
using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/[controller]")]
[Produces("application/json")]
public class TracksController : ControllerBase
{
    private readonly MusicService _musicService;
    private readonly ILogger<TracksController> _logger;
    private readonly FavoritesService _favoritesService;
    private readonly ICacheService _cache;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    private static readonly TimeSpan WarmupDebounce = TimeSpan.FromSeconds(30);

    public TracksController(
        MusicService musicService,
        ILogger<TracksController> logger,
        FavoritesService favoritesService,
        ICacheService cache,
        IBackgroundJobClient backgroundJobs,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _musicService = musicService;
        _logger = logger;
        _favoritesService = favoritesService;
        _cache = cache;
        _backgroundJobs = backgroundJobs;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    private async Task TriggerWarmupIfIdleAsync(CancellationToken cancellationToken)
    {
        if (await _cache.TryAcquireLockAsync(CacheKeys.WarmupLock, WarmupDebounce, cancellationToken))
        {
            _backgroundJobs.Enqueue<CacheWarmupService>(service => service.WarmUpAsync(CancellationToken.None));
            _logger.LogInformation("Cold cache: enqueued background warmup job.");
        }
    }

    [HttpGet("me")]
    [ProducesResponseType(typeof(PagedResultDto<TrackDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyTracks(
        [FromQuery] string? search,
        [FromQuery] string? genre,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {

        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();
        if (!userRole.HasRole(AppRoles.Artist) && !userRole.HasRole(AppRoles.Admin))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Тільки артисти мають власні треки." });

        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var (tracks, totalCount) = await GetTracksWithCacheAsync(
            search, currentUserId, genre, null, pageNumber, pageSize, cancellationToken);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var likedIds = await _favoritesService.GetLikedTrackIdsAsync(currentUserId, cancellationToken);

        var trackDtos = tracks
            .Select(t => MapToDto(t, baseUrl, likedIds.Contains(t.Id)))
            .ToList();

        return Ok(new PagedResultDto<TrackDto>(trackDtos, totalCount, pageNumber, pageSize));
    }

    // GET music/tracks/genres — реальные значения Genre, которые сейчас есть в БД,
    // чтобы фронтенд не хардкодил список и не расходился с бэком.
    [HttpGet("genres")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGenres(CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetAsync<List<string>>(CacheKeys.Genres, cancellationToken);
        if (cached is not null)
            return Ok(cached);

        var genres = await _musicService.GetDistinctGenresAsync(cancellationToken);
        await _cache.SetAsync(CacheKeys.Genres, genres.ToList(), TimeSpan.FromMinutes(30), cancellationToken);
        return Ok(genres);
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<TrackDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllTracks(
        [FromQuery] string? search,
        [FromQuery] Guid? userId,
        [FromQuery] string? genre,
        [FromQuery] string? artist,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        var (tracks, totalCount) = await GetTracksWithCacheAsync(
            search, userId, genre, artist, pageNumber, pageSize, cancellationToken);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        HttpContext.TryGetUserId(out var currentUserId);

        var likedIds = currentUserId != Guid.Empty
            ? await _favoritesService.GetLikedTrackIdsAsync(currentUserId, cancellationToken)
            : new HashSet<Guid>();

        var trackDtos = tracks
            .Select(t => MapToDto(t, baseUrl, likedIds.Contains(t.Id)))
            .ToList();

        var result = new PagedResultDto<TrackDto>(trackDtos, totalCount, pageNumber, pageSize);
        return Ok(result);
    }

    [HttpGet("popular")]
    [ProducesResponseType(typeof(PagedResultDto<TrackDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPopularTracks(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        var skip = (pageNumber - 1) * pageSize;
        var warmTrending = await _cache.GetAsync<CachedTrackPage>(CacheKeys.TrendingTop, cancellationToken);

        // Холодный кеш: не выполняем тяжёлый SQL в запросе, ставим фоновый прогрев
        // и мгновенно отдаём пустую страницу — фронтенд перезапросит через пару секунд.
        if (warmTrending is null)
        {
            await TriggerWarmupIfIdleAsync(cancellationToken);
            return Ok(new PagedResultDto<TrackDto>(new List<TrackDto>(), 0, pageNumber, pageSize));
        }

        var tracks = warmTrending.Items.Skip(skip).Take(pageSize).Select(c => c.ToTrack()).ToList();
        var totalCount = warmTrending.TotalCount;

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        HttpContext.TryGetUserId(out var currentUserId);

        var likedIds = currentUserId != Guid.Empty
            ? await _favoritesService.GetLikedTrackIdsAsync(currentUserId, cancellationToken)
            : new HashSet<Guid>();

        var trackDtos = tracks
            .Select(t => MapToDto(t, baseUrl, likedIds.Contains(t.Id)))
            .ToList();

        return Ok(new PagedResultDto<TrackDto>(trackDtos, totalCount, pageNumber, pageSize));
    }

    // GET music/tracks/recommendations — секция "Музыка по стилю и настроению" на главной.
    [HttpGet("recommendations")]
    [ProducesResponseType(typeof(List<MoodRecommendationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMoodRecommendations(
        [FromQuery] int take = 8,
        CancellationToken cancellationToken = default)
    {
        if (take < 1) take = 1;
        if (take > 50) take = 50;

        var cacheKey = CacheKeys.RecommendationsMood(take);
        var groups = await _cache.GetAsync<List<CachedMoodGroup>>(cacheKey, cancellationToken);

        if (groups is null)
        {
            await TriggerWarmupIfIdleAsync(cancellationToken);
            return Ok(new List<MoodRecommendationDto>());
        }

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        HttpContext.TryGetUserId(out var currentUserId);
        var likedIds = currentUserId != Guid.Empty
            ? await _favoritesService.GetLikedTrackIdsAsync(currentUserId, cancellationToken)
            : new HashSet<Guid>();

        var response = groups
            .Select(g => new MoodRecommendationDto
            {
                Mood = g.Mood,
                Tracks = g.Tracks.Select(c => MapToDto(c.ToTrack(), baseUrl, likedIds.Contains(c.Id))).ToList()
            })
            .ToList();

        return Ok(response);
    }

    // GET music/tracks/recommendations/personalized — персональна підбірка на основі
    // топ-2 жанрів/настроїв із історії прослуховувань та улюблених треків користувача.
    [HttpGet("recommendations/personalized")]
    [ProducesResponseType(typeof(PagedResultDto<TrackDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPersonalizedRecommendations(
        [FromQuery] int take = 12,
        CancellationToken cancellationToken = default)
    {
        if (take < 1) take = 1;
        if (take > 50) take = 50;

        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var likedIds = await _favoritesService.GetLikedTrackIdsAsync(currentUserId, cancellationToken);
        var historyTrackIds = await FetchRecentHistoryTrackIdsAsync(currentUserId, cancellationToken);

        List<Track> resultTracks;

        if (historyTrackIds.Count < 5)
        {
            resultTracks = await GetTrendingFallbackAsync(take, cancellationToken);
        }
        else
        {
            var signalTrackIds = historyTrackIds.Concat(likedIds).Distinct().ToList();
            resultTracks = await _musicService.GetPersonalizedRecommendationsAsync(signalTrackIds, take, cancellationToken);

            if (resultTracks.Count == 0)
            {
                resultTracks = await GetTrendingFallbackAsync(take, cancellationToken);
            }
        }

        var dtos = resultTracks.Select(t => MapToDto(t, baseUrl, likedIds.Contains(t.Id))).ToList();
        return Ok(new PagedResultDto<TrackDto>(dtos, dtos.Count, 1, take));
    }

    private async Task<List<Track>> GetTrendingFallbackAsync(int take, CancellationToken cancellationToken)
    {
        var warmTrending = await _cache.GetAsync<CachedTrackPage>(CacheKeys.TrendingTop, cancellationToken);
        if (warmTrending is null)
        {
            await TriggerWarmupIfIdleAsync(cancellationToken);
            return new List<Track>();
        }
        return warmTrending.Items.Take(take).Select(c => c.ToTrack()).ToList();
    }

    private async Task<List<Guid>> FetchRecentHistoryTrackIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var historyBaseUrl = (_configuration["History:BaseUrl"] ?? "https://localhost:7232").TrimEnd('/');
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());

            var response = await client.GetAsync(
                $"{historyBaseUrl}/api/history?pageNumber=1&pageSize=50", cancellationToken);
            if (!response.IsSuccessStatusCode) return new List<Guid>();

            var payload = await response.Content.ReadFromJsonAsync<HistoryResponseDto>(
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            return payload?.Items?.Select(i => i.TrackId).Distinct().ToList() ?? new List<Guid>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не вдалося отримати історію прослуховувань для персональних рекомендацій, використовуємо тільки лайки.");
            return new List<Guid>();
        }
    }

    private class HistoryResponseDto
    {
        public List<HistoryItemDto> Items { get; set; } = new();
    }

    private class HistoryItemDto
    {
        public Guid TrackId { get; set; }
    }

    [HttpGet("deleted")]
    public async Task<IActionResult> GetDeletedTracks(CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { Error = "Потрібна авторизація." });

        var tracks = await _musicService.GetDeletedTracksAsync(currentUserId, cancellationToken);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var trackDtos = tracks.Select(t => MapToDto(t, baseUrl, isLiked: false)).ToList();
        return Ok(trackDtos);
    }
    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> RestoreTrack(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { Error = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();

        try
        {
            var result = await _musicService.RestoreTrackAsync(id, currentUserId, userRole, cancellationToken);
            if (!result) return NotFound(new { Error = "Видалений трек не знайдено." });

            return Ok(new { message = "Трек успішно відновлено." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Error = ex.Message });
        }
    }

    // DELETE music/tracks/{id}/permanent — окончательное удаление уже soft-deleted трека,
    // не дожидаясь фоновой очистки.
    [HttpDelete("{id:guid}/permanent")]
    public async Task<IActionResult> PermanentlyDeleteTrack(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { Error = "Потрібна авторизація." });

        var userRole = Request.Headers["X-User-Role"].ToString();

        try
        {
            var result = await _musicService.PermanentlyDeleteTrackAsync(id, currentUserId, userRole, cancellationToken);
            if (!result) return NotFound(new { Error = "Видалений трек не знайдено." });

            return Ok(new { message = "Трек остаточно видалено." });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Error = ex.Message });
        }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TrackDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTrackById(Guid id, CancellationToken cancellationToken)
    {
        var track = await GetTrackWithCacheAsync(id, cancellationToken);

        if (track is null)
            return NotFound(new { Error = $"Трек с id '{id}' не найден." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        return Ok(MapToDto(track, baseUrl));
    }

    [HttpGet("{id:guid}/lyrics")]
    [ProducesResponseType(typeof(LyricsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLyrics(
        Guid id,
        [FromServices] LyricsService lyricsService,
        CancellationToken cancellationToken = default)
    {
        // Намеренно не через кеш: LyricsService сам замыкает короткий путь по track.LyricsLrc,
        // а CachedTrack это поле не хранит — прогон через Redis каждый раз обнулял бы его.
        var track = await _musicService.GetTrackByIdAsync(id, cancellationToken);
        if (track is null)
            return NotFound(new { Error = $"Трек с id '{id}' не найден." });

        var lrc = await lyricsService.GetOrCreateLyricsAsync(track, cancellationToken);

        return Ok(new LyricsResponseDto
        {
            TrackId = track.Id,
            Title = track.Title,
            Artist = track.ArtistName,
            Lrc = lrc ?? string.Empty,
            IsInstrumental = string.IsNullOrWhiteSpace(lrc)
        });
    }

    [HttpGet("{id:guid}/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    public async Task<IActionResult> StreamTrack(Guid id, CancellationToken cancellationToken = default)
    {
        // Публичный эндпоинт — доступен и незарегистрированным пользователям (гостевое
        // прослушивание). Учёт прослушиваний по конкретному юзеру всё равно происходит
        // только через отдельный авторизованный POST /{id}/play.
        var track = await GetTrackWithCacheAsync(id, cancellationToken);
        if (track is null)
            return NotFound(new { Error = "Трек не найден." });

        if (track.IsExternal)
        {
            _logger.LogInformation("Stream (External): редирект трека {TrackId} на Jamendo URL.", id);

            return Redirect(track.ExternalAudioUrl!);
        }

        var fileInfo = await _musicService.GetTrackFileInfoAsync(id, cancellationToken);
        if (fileInfo is null) return NotFound();

        var (absolutePath, contentType) = fileInfo.Value;
        if (!System.IO.File.Exists(absolutePath)) return NotFound();

        Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        return PhysicalFile(absolutePath, contentType, enableRangeProcessing: true);
    }

    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadTrack(Guid id, CancellationToken cancellationToken = default)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Download requires authentication." });

        var track = await GetTrackWithCacheAsync(id, cancellationToken);
        if (track is null)
            return NotFound(new { Error = "Трек не найден." });

        if (track.IsExternal)
        {
            _logger.LogInformation("Download (External): редирект трека {TrackId} на Jamendo URL.", id);
            return Redirect(track.ExternalAudioUrl!);
        }

        var fileInfo = await _musicService.GetTrackFileInfoAsync(id, cancellationToken);
        if (fileInfo is null) return NotFound();

        var (absolutePath, contentType) = fileInfo.Value;
        if (!System.IO.File.Exists(absolutePath)) return NotFound();

        var safeFileName = string.Join("_", $"{track.ArtistName} - {track.Title}".Split(Path.GetInvalidFileNameChars()));
        var extension = Path.GetExtension(absolutePath);

        return PhysicalFile(absolutePath, contentType, $"{safeFileName}{extension}", enableRangeProcessing: false);
    }

    [HttpPost("{id:guid}/play")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkTrackAsPlayed(Guid id, CancellationToken cancellationToken = default)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var trackExists = await GetTrackWithCacheAsync(id, cancellationToken);
        if (trackExists is null)
            return NotFound(new { Error = "Трек не найден." });

        var result = await _musicService.IncrementPlayCountAsync(userId, id, cancellationToken);
        if (!result)
            return NotFound(new { Error = "Трек не найден." });

        return Ok(new { message = "Play count updated." });
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteTrack(Guid id, CancellationToken cancellationToken)
    {

        var userRole = Request.Headers["X-User-Role"].ToString();

        if (!HttpContext.TryGetUserId(out var currentUserId))
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

    [HttpPatch("{id:guid}")]
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
    if (!ModelState.IsValid)
        return ValidationProblem(ModelState);

    if (string.IsNullOrWhiteSpace(dto.Title))
        return BadRequest(new { Error = "New title cannot be empty." });

    var userRole = Request.Headers["X-User-Role"].ToString();

    if (!HttpContext.TryGetUserId(out var currentUserId))
    {
        return Unauthorized(new { Error = "User identity is missing or invalid." });
    }

    try
    {
        var track = await _musicService.RenameTrackAsync(
            id,
            dto.Title,
            currentUserId,
            userRole,
            cancellationToken);

        if (track is null)
        {
            return NotFound(new { Error = $"Трек с id '{id}' не найден." });
        }

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(MapToDto(track, baseUrl));
    }
    catch (UnauthorizedAccessException ex)
    {
        return StatusCode(StatusCodes.Status403Forbidden, new { Error = ex.Message });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unexpected error while renaming track {TrackId}", id);
        return StatusCode(StatusCodes.Status500InternalServerError, new { Error = "An unexpected error occurred." });
    }
}

    private static TrackDto MapToDto(Track track, string baseUrl, bool isLiked = false)
    {
        var audioUrl = $"{baseUrl}/music/tracks/{track.Id}/stream";
        string? coverUrl = null;
        if (track.IsExternal)
            coverUrl = track.ExternalCoverUrl;
        else if (!string.IsNullOrWhiteSpace(track.CoverImageRelativePath))
            coverUrl = $"{baseUrl}/music/files/{track.CoverImageRelativePath.Replace('\\', '/')}";
        return new TrackDto
        {
            TrackId         = track.Id,
            Title           = track.Title,
            ArtistName      = track.ArtistName,
            Album           = track.AlbumTitle ?? track.Album?.Title,
            Genre           = track.Genre,
            Mood            = track.Mood,
            DurationSeconds = track.DurationSeconds,
            FileSizeBytes   = track.FileSizeBytes,
            ContentType     = track.ContentType,
            AudioUrl        = audioUrl,
            CoverImageUrl   = coverUrl,
            UploadedAt      = track.UploadedAt,
            PlayCount       = track.PlayCount,
            IsLiked         = isLiked
        };
    }

    private async Task<(IReadOnlyList<Track> Items, int TotalCount)> GetTracksWithCacheAsync(
        string? search, Guid? userId, string? genre, string? artist,
        int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        var key = CacheKeys.TracksSearch(search, userId, genre, artist, pageNumber, pageSize);
        var cached = await _cache.GetAsync<CachedTrackPage>(key, cancellationToken);
        if (cached is not null)
            return (cached.Items.Select(c => c.ToTrack()).ToList(), cached.TotalCount);

        var (items, totalCount) = await _musicService.GetAllTracksAsync(
            search, userId, genre, artist, pageNumber, pageSize, cancellationToken);

        var payload = new CachedTrackPage
        {
            Items = items.Select(CachedTrack.FromTrack).ToList(),
            TotalCount = totalCount
        };
        await _cache.SetAsync(key, payload, TimeSpan.FromMinutes(5), cancellationToken);

        return (items, totalCount);
    }

    private async Task<Track?> GetTrackWithCacheAsync(Guid id, CancellationToken cancellationToken)
    {
        var key = CacheKeys.Track(id);
        var cached = await _cache.GetAsync<CachedTrack>(key, cancellationToken);
        if (cached is not null)
            return cached.ToTrack();

        var track = await _musicService.GetTrackByIdAsync(id, cancellationToken);
        if (track is not null)
            await _cache.SetAsync(key, CachedTrack.FromTrack(track), TimeSpan.FromMinutes(10), cancellationToken);

        return track;
    }
}
