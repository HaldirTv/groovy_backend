using System.Text;
using System.Text.Json.Serialization;
using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

/// <summary>
/// Ищет синхронизированный текст песни (LRC) для трека: сначала кэш в БД, затем публичный
/// каталог LRCLIB (четыре стратегии поиска, см. TryFetchFromLrclibAsync). Если у LRCLIB есть
/// только неразмеченный текст — раскладываем его по длительности трека. Если ничего не
/// найдено, кэшируем пустую строку, чтобы не искать повторно.
/// </summary>
public class LyricsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LyricsService> _logger;

    public LyricsService(IHttpClientFactory httpClientFactory, IServiceScopeFactory scopeFactory, ILogger<LyricsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<string?> GetOrCreateLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track.LyricsLrc is not null)
            return track.LyricsLrc;

        var lrc = await TryFetchFromLrclibAsync(track, cancellationToken) ?? string.Empty;
        await CacheLyricsAsync(track.Id, lrc, cancellationToken);
        return lrc;
    }

    /// <summary>
    /// Четыре стратегии подряд, от самой точной к самой широкой. Прерываемся на первой,
    /// которая что-то вернула (включая пустую строку — это подтверждённый инструментал).
    /// </summary>
    private async Task<string?> TryFetchFromLrclibAsync(Track track, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("lrclib");
        try
        {
            // 1. Точное совпадение по всем метаданным — альбом и длительность отсеивают
            //    каверы и ремиксы с тем же названием.
            var albumName = track.AlbumTitle ?? track.Album?.Title;
            var exactUrl = $"/api/get?artist_name={Esc(track.ArtistName)}&track_name={Esc(track.Title)}"
                           + (string.IsNullOrWhiteSpace(albumName) ? string.Empty : $"&album_name={Esc(albumName)}")
                           + $"&duration={(int)track.DurationSeconds}";
            if (await QueryGetAsync(client, exactUrl, track.DurationSeconds, cancellationToken) is { } exact)
                return exact;

            // 2. Без альбома и длительности — метаданные загруженных файлов часто неполные.
            var looseUrl = $"/api/get?artist_name={Esc(track.ArtistName)}&track_name={Esc(track.Title)}";
            if (await QueryGetAsync(client, looseUrl, track.DurationSeconds, cancellationToken) is { } loose)
                return loose;

            // 3. Полнотекстовый поиск "артист + название".
            var combinedQuery = $"{track.ArtistName} {track.Title}".Trim();
            if (await QuerySearchAsync(client, combinedQuery, track.DurationSeconds, cancellationToken) is { } combined)
                return combined;

            // 4. Поиск по одному названию — спасает треки, залитые с ArtistName вида "User".
            if (!string.IsNullOrWhiteSpace(track.Title) && track.Title != combinedQuery &&
                await QuerySearchAsync(client, track.Title, track.DurationSeconds, cancellationToken) is { } byTitle)
                return byTitle;

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LRCLIB lookup failed for track {TrackId}", track.Id);
            return null;
        }
    }

    private static async Task<string?> QueryGetAsync(HttpClient client, string url, double durationSeconds, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var dto = await response.Content.ReadFromJsonAsync<LrclibResponseDto>(cancellationToken: cancellationToken);
        return PickLyrics(dto, durationSeconds);
    }

    private static async Task<string?> QuerySearchAsync(HttpClient client, string query, double durationSeconds, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"/api/search?q={Esc(query)}", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var results = await response.Content.ReadFromJsonAsync<List<LrclibResponseDto>>(cancellationToken: cancellationToken);
        if (results is null || results.Count == 0) return null;

        // Наличие синхронизированного текста важнее позиции в выдаче: сначала ищем LRC
        // по всему списку и только потом откатываемся на обычный текст.
        var synced = results.FirstOrDefault(r => HasText(r.SyncedLyrics));
        if (synced is not null) return synced.SyncedLyrics;

        var plain = results.FirstOrDefault(r => HasText(r.PlainLyrics));
        return plain is null ? null : FormatPlainLyricsAsLrc(plain.PlainLyrics!, durationSeconds);
    }

    private static string? PickLyrics(LrclibResponseDto? dto, double durationSeconds)
    {
        if (dto is null) return null;
        if (HasText(dto.SyncedLyrics))
            return dto.SyncedLyrics;
        if (dto.Instrumental)
            return string.Empty;
        if (HasText(dto.PlainLyrics))
            return FormatPlainLyricsAsLrc(dto.PlainLyrics!, durationSeconds);
        return null;
    }

    /// <summary>
    /// Раскладывает неразмеченный текст равномерно по длительности трека, чтобы плеер
    /// мог подсвечивать строки. Тайминги приблизительные — это лучше, чем ничего.
    /// </summary>
    private static string FormatPlainLyricsAsLrc(string plainLyrics, double durationSeconds)
    {
        var lines = plainLyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return string.Empty;

        var step = durationSeconds > 0 ? durationSeconds / lines.Length : 3.0;
        var builder = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var currentSec = i * step;
            var minutes = (int)(currentSec / 60);
            var seconds = (int)(currentSec % 60);
            var hundredths = (int)((currentSec - Math.Floor(currentSec)) * 100);
            builder.AppendLine($"[{minutes:00}:{seconds:00}.{hundredths:00}] {lines[i]}");
        }
        return builder.ToString();
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length > 10;

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private async Task CacheLyricsAsync(Guid trackId, string lrc, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
        await db.Tracks
            .Where(t => t.Id == trackId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.LyricsLrc, lrc), cancellationToken);
    }

    private class LrclibResponseDto
    {
        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("instrumental")]
        public bool Instrumental { get; set; }
    }
}
