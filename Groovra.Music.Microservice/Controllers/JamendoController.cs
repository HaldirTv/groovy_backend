using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    private const string JamendoClientId = "16efb5ab";

    // Верхняя граница случайного offset внутри каталога Jamendo —
    // просто "глубина", из которой берём случайный кусок, не реальный размер базы.
    private const int MaxOffsetRange = 5000;

    public JamendoController(MusicDbContext context, HttpClient httpClient)
    {
        _context = context;
        _httpClient = httpClient;
    }

    [HttpPost("seed-popular")]
    public async Task<IActionResult> SeedPopular([FromQuery] int limit = 10)
    {
        var url = $"https://api.jamendo.com/v3.0/tracks/?client_id={JamendoClientId}&format=json&limit={limit}&audioformat=mp32&imagesize=200&order=popularity_week";
        return await FetchAndSeed(url);
    }

    /// <summary>
    /// POST /music/jamendo/seed-random?count=50
    /// POST /music/jamendo/seed-random?count=50&tag=rock
    /// Без tag — берёт случайный кусок каталога (рандомный offset на каждый вызов).
    /// С tag — берёт треки конкретного жанра/тега (rock, jazz, electronic, ambient, hiphop, classical, lounge, и т.д.)
    /// </summary>
    [HttpPost("seed-random")]
    public async Task<IActionResult> SeedRandom([FromQuery] int count = 50, [FromQuery] string? tag = null)
    {
        if (count < 1) count = 50;
        if (count > 200) count = 200; // у Jamendo лимит на один запрос — 200

        var randomOffset = Random.Shared.Next(0, MaxOffsetRange);

        var url = $"https://api.jamendo.com/v3.0/tracks/?client_id={JamendoClientId}&format=json&limit={count}&offset={randomOffset}&audioformat=mp32&imagesize=200";

        if (!string.IsNullOrWhiteSpace(tag))
        {
            url += $"&fuzzytags={Uri.EscapeDataString(tag)}";
        }

        return await FetchAndSeed(url);
    }

    // ─── Общая логика запроса к Jamendo + сохранения в БД ──────────────────

    private async Task<IActionResult> FetchAndSeed(string url)
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
                    Album = jTrack.AlbumName ?? "Single",
                    Genre = "Jamendo Track",
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

            if (addedCount > 0) await _context.SaveChangesAsync();

            return Ok(new { Message = $"Успешно добавлено треков: {addedCount}" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Внутренняя ошибка: {ex.Message}");
        }
    }
}

// ─── Модели остаются без изменений ─────────────────────────────────

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
}