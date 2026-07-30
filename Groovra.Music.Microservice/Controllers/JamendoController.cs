using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.Model;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/[controller]")]
public class JamendoController : ControllerBase
{
    private readonly MusicDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly ICacheService _cache;

    private readonly IConfiguration _configuration;

    private readonly string JamendoClientId;
    private const int MaxOffsetRange = 5000;

    public JamendoController(MusicDbContext context, HttpClient httpClient, IConfiguration configuration, ICacheService cache)
    {
        _context = context;
        _httpClient = httpClient;
        _configuration = configuration;
        _cache = cache;
        JamendoClientId = _configuration.GetSection("JamendoApi:ClientId").Value ?? "";
    }

    private async Task InvalidateTrackListCachesAsync()
    {
        await _cache.RemoveAsync(CacheKeys.Genres);
        foreach (var pattern in CacheKeys.ListPatterns)
        {
            await _cache.RemoveByPatternAsync(pattern);
        }
    }

    [HttpPost("seed-popular")]
    public async Task<IActionResult> SeedPopular([FromQuery] int limit = 10)
    {
        var url = $"https://api.jamendo.com/v3.0/tracks/?client_id={JamendoClientId}&format=json&limit={limit}&audioformat=mp32&imagesize=200&order=popularity_week&include=musicinfo";
        return await FetchAndSeed(url);
    }

    [HttpPost("seed-random")]
    public async Task<IActionResult> SeedRandom([FromQuery] int count = 50, [FromQuery] string? tag = null)
    {
        if (count < 1) count = 50;
        if (count > 200) count = 200;

        var randomOffset = Random.Shared.Next(0, MaxOffsetRange);

        var url = $"https://api.jamendo.com/v3.0/tracks/?client_id={JamendoClientId}&format=json&limit={count}&offset={randomOffset}&audioformat=mp32&imagesize=200&include=musicinfo";

        if (!string.IsNullOrWhiteSpace(tag))
        {
            url += $"&fuzzytags={Uri.EscapeDataString(tag)}";
        }

        return await FetchAndSeed(url, fallbackGenre: tag);
    }

    [HttpPost("seed-albums-random")]
public async Task<IActionResult> SeedAlbumsRandom([FromQuery] int count = 10, [FromQuery] string? tag = null)
{
    if (count < 1) count = 10;
    if (count > 50) count = 50;

    int currentMaxOffset = string.IsNullOrWhiteSpace(tag) ? 2000 : 200;
    var randomOffset = Random.Shared.Next(0, currentMaxOffset);

    var url = $"https://api.jamendo.com/v3.0/albums/tracks/?client_id={JamendoClientId}&format=json&limit={count}&offset={randomOffset}&audioformat=mp32&imagesize=200&include=musicinfo";

    if (!string.IsNullOrWhiteSpace(tag))
    {
        url += $"&tags={Uri.EscapeDataString(tag)}";
    }

    try
    {
        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, $"Jamendo API упал: {response.StatusCode}. Ответ: {errorContent}");
        }
        var jsonString = await response.Content.ReadAsStringAsync();

        var jamendoData = JsonSerializer.Deserialize<JamendoAlbumResponse>(jsonString, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        });

        if (jamendoData?.Headers != null && jamendoData.Headers.Status == "failed")
        {
            return BadRequest(new
            {
                Error = "Jamendo API вернул ошибку внутри JSON!",
                Reason = jamendoData.Headers.ErrorMessage,
                Code = jamendoData.Headers.Code
            });
        }

        if (jamendoData?.Results == null || !jamendoData.Results.Any())
        {
            return NotFound(new { Error = "Альбомы не найдены, но Jamendo говорит, что всё ок." });
        }

        var systemUserId = Guid.Parse("00000000-0000-0000-0000-000000000000");
        var addedAlbumsCount = 0;
        var addedTracksCount = 0;

        var albumsSeenInThisBatch = new HashSet<(string Name, string Artist)>();

        foreach (var jAlbum in jamendoData.Results)
        {
            var albumKey = (jAlbum.Name, jAlbum.ArtistName);
            if (!albumsSeenInThisBatch.Add(albumKey))
                continue;

            var albumExists = await _context.Albums.AnyAsync(a => a.Title == jAlbum.Name && a.ArtistName == jAlbum.ArtistName && !a.IsDeleted);
            if (albumExists) continue;

            var newAlbum = new Album
            {
                Id = Guid.NewGuid(),
                UserId = systemUserId,
                Title = jAlbum.Name,
                ArtistName = jAlbum.ArtistName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Tracks = new List<Track>()
            };

            if (DateOnly.TryParse(jAlbum.ReleaseDate, out var releaseDate))
            {
                newAlbum.ReleaseDate = releaseDate;
            }

            double totalDuration = 0;

            if (jAlbum.Tracks == null || !jAlbum.Tracks.Any())
                continue;

            foreach (var jTrack in jAlbum.Tracks)
            {
                if (string.IsNullOrWhiteSpace(jTrack.Audio))
                    continue;

                var existingTrack = _context.Tracks.Local.FirstOrDefault(t => t.ExternalAudioUrl == jTrack.Audio)
                                    ?? await _context.Tracks.FirstOrDefaultAsync(t => t.ExternalAudioUrl == jTrack.Audio);

                if (existingTrack != null)
                {
                    if (existingTrack.AlbumId != null && existingTrack.AlbumId != Guid.Empty)
                        continue;

                    existingTrack.AlbumId = newAlbum.Id;
                    existingTrack.AlbumTitle = newAlbum.Title;

                    newAlbum.Tracks.Add(existingTrack);
                    totalDuration += jTrack.Duration;
                    continue;
                }

                var newTrack = new Track
                {
                    Id = Guid.NewGuid(),
                    Title = jTrack.Name,
                    ArtistName = jAlbum.ArtistName,
                    AlbumTitle = jAlbum.Name,
                    AlbumId = newAlbum.Id,
                    Genre = ResolveGenre(jTrack.MusicInfo, tag),
                    DurationSeconds = jTrack.Duration,
                    FileSizeBytes = 0,
                    ContentType = "audio/mpeg",
                    IsExternal = true,
                    ExternalAudioUrl = jTrack.Audio,
                    ExternalCoverUrl = jAlbum.Image,
                    UserId = systemUserId,
                    PlayCount = 0,
                    UploadedAt = DateTime.UtcNow
                };

                newAlbum.Tracks.Add(newTrack);
                totalDuration += jTrack.Duration;
                addedTracksCount++;
            }

            if (newAlbum.Tracks.Any())
            {
                newAlbum.TrackCount = newAlbum.Tracks.Count;
                newAlbum.TotalDurationSeconds = totalDuration;
                _context.Albums.Add(newAlbum);
                addedAlbumsCount++;
            }
        }

        if (addedAlbumsCount > 0)
        {
            await _context.SaveChangesAsync();
            await InvalidateTrackListCachesAsync();
        }

        return Ok(new
        {
            Message = $"Успешно добавлено альбомов: {addedAlbumsCount}. Из них новых треков создано: {addedTracksCount}"
        });
    }
    catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
    {
        var realReason = ex.InnerException?.Message ?? ex.Message;
        return StatusCode(500, new
        {
            Error = "Ошибка при сохранении в БД (DbUpdateException).",
            Reason = realReason
        });
    }
    catch (JsonException ex)
    {
        return StatusCode(500, new
        {
            Error = "Не удалось распарсить ответ Jamendo (JsonException).",
            Reason = ex.Message
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new
        {
            Error = "Внутренняя ошибка.",
            Reason = ex.Message,
            Type = ex.GetType().Name
        });
    }
}

    // Достаём жанр из musicinfo.tags.genres, которые отдаёт Jamendo при include=musicinfo.
    // Если API их не вернул — падаем на tag из запроса, иначе null.
    private static string? ResolveGenre(JamendoMusicInfoDto? musicInfo, string? fallback)
    {
        var genre = musicInfo?.Tags?.Genres?.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
        if (!string.IsNullOrWhiteSpace(genre)) return genre;
        return !string.IsNullOrWhiteSpace(fallback) ? fallback : null;
    }

    private async Task<IActionResult> FetchAndSeed(string url, string? fallbackGenre = null)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, "Jamendo API вообще лежит или недоступно.");
            }

            var jsonString = await response.Content.ReadAsStringAsync();

            var jamendoData = JsonSerializer.Deserialize<JamendoResponse>(jsonString, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (jamendoData?.Headers != null && jamendoData.Headers.Status == "failed")
            {
                return BadRequest(new
                {
                    Error = "Jamendo API вернул ошибку внутри JSON!",
                    Reason = jamendoData.Headers.ErrorMessage,
                    Code = jamendoData.Headers.Code
                });
            }

            if (jamendoData?.Results == null || !jamendoData.Results.Any())
            {
                return NotFound(new { Error = "Треки не найдены, но Jamendo говорит, что всё ок. Странно." });
            }

            var systemUserId = Guid.Parse("00000000-0000-0000-0000-000000000000");
            var addedCount = 0;

            foreach (var jTrack in jamendoData.Results)
            {
                var exists = await _context.Tracks.AnyAsync(t => t.ExternalAudioUrl == jTrack.Audio);
                if (exists) continue;

                var newTrack = new Track
                {
                    Id = Guid.NewGuid(),
                    Title = jTrack.Name,
                    ArtistName = jTrack.ArtistName,
                    AlbumTitle = jTrack.AlbumName ?? "Single",
                    Genre = ResolveGenre(jTrack.MusicInfo, fallbackGenre),
                    DurationSeconds = jTrack.Duration,
                    FileSizeBytes = 0,
                    ContentType = "audio/mpeg",
                    IsExternal = true,
                    ExternalAudioUrl = jTrack.Audio,
                    ExternalCoverUrl = jTrack.Image,
                    AudioRelativePath = null,
                    CoverImageRelativePath = null,
                    UserId = systemUserId,
                    PlayCount = 0,
                    UploadedAt = DateTime.UtcNow
                };

                _context.Tracks.Add(newTrack);
                addedCount++;
            }

            if (addedCount > 0)
            {
                await _context.SaveChangesAsync();
                await InvalidateTrackListCachesAsync();
            }

            return Ok(new { Message = $"Успешно добавлено треков: {addedCount}" });
        }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
            {
                var realReason = ex.InnerException?.Message ?? ex.Message;
                return StatusCode(500, new
                {
                    Error = "Ошибка при сохранении в БД (DbUpdateException).",
                    Reason = realReason
                });
            }
            catch (JsonException ex)
            {
                return StatusCode(500, new
                {
                    Error = "Не удалось распарсить ответ Jamendo (JsonException).",
                    Reason = ex.Message
                });
            }
        catch (Exception ex)
        {
            return StatusCode(500, $"Внутренняя ошибка: {ex.Message}");
        }
    }
}

public class JamendoResponse
{
    public JamendoHeaderDto Headers { get; set; } = new();
    public List<JamendoTrackDto> Results { get; set; } = new();
}

public class JamendoHeaderDto
{
    public string Status { get; set; } = string.Empty;
    public int Code { get; set; }

    [JsonPropertyName("error_message")]
    public string ErrorMessage { get; set; } = string.Empty;
}

public class JamendoTrackDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Duration { get; set; }

    [JsonPropertyName("artist_name")]
    public string ArtistName { get; set; } = string.Empty;

    [JsonPropertyName("album_name")]
    public string AlbumName { get; set; } = string.Empty;

    public string Audio { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;

    [JsonPropertyName("musicinfo")]
    public JamendoMusicInfoDto? MusicInfo { get; set; }
}

public class JamendoMusicInfoDto
{
    public JamendoTagsDto? Tags { get; set; }
}

public class JamendoTagsDto
{
    public List<string> Genres { get; set; } = new();
}

public class JamendoAlbumResponse
{
    public JamendoHeaderDto Headers { get; set; } = new();
    public List<JamendoAlbumDto> Results { get; set; } = new();
}

public class JamendoAlbumDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("releasedate")]
    public string ReleaseDate { get; set; } = string.Empty;

    [JsonPropertyName("artist_name")]
    public string ArtistName { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;
    public List<JamendoAlbumTrackDto> Tracks { get; set; } = new();
}

public class JamendoAlbumTrackDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Duration { get; set; }
    public string Audio { get; set; } = string.Empty;

    [JsonPropertyName("musicinfo")]
    public JamendoMusicInfoDto? MusicInfo { get; set; }
}
