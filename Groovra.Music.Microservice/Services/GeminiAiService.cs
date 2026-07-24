using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Shared.ServiceResult;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

/// <summary>
/// Генерирует плейлист-подборку из каталога треков по текстовому промпту пользователя,
/// используя Gemini. Если ключ API не настроен или запрос к Gemini не удался — откатывается
/// на простой mock-подбор по ключевым словам, чтобы фича оставалась рабочей без внешнего API.
/// </summary>
public class GeminiAiService
{
    private readonly MusicDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiAiService> _logger;

    public GeminiAiService(MusicDbContext db, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<GeminiAiService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ServiceResult<PlaylistDto>> GenerateAiMixAsync(
        Guid userId, string userPrompt, string baseUrl, CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["Gemini:ApiKey"];
        bool isMock = string.IsNullOrWhiteSpace(apiKey) ||
                      apiKey.Contains("YOUR_GEMINI", StringComparison.OrdinalIgnoreCase) ||
                      apiKey.Equals("mock", StringComparison.OrdinalIgnoreCase);

        var catalogTracks = await _db.Tracks
            .AsNoTracking()
            .OrderByDescending(t => t.PlayCount)
            .Take(60)
            .Select(t => new CatalogTrackInfo(t.Id, t.Title, t.ArtistName, t.Genre, t.DurationSeconds))
            .ToListAsync(cancellationToken);

        if (catalogTracks.Count == 0)
            return ServiceResult<PlaylistDto>.Fail("У каталозі ще немає треків для підбору.");

        List<Guid>? orderedTrackIds = null;
        string? title = null;
        string? description = null;

        if (!isMock)
        {
            try
            {
                (orderedTrackIds, title, description) = await CallGeminiAsync(apiKey!, userPrompt, catalogTracks, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gemini AI mix request failed, falling back to mock selection.");
            }
        }

        if (orderedTrackIds is null || orderedTrackIds.Count == 0)
        {
            (orderedTrackIds, title, description) = GenerateMockSelection(userPrompt, catalogTracks);
        }

        return await CreatePlaylistFromAiDataAsync(userId, title!, description, orderedTrackIds, baseUrl, cancellationToken);
    }

    private async Task<(List<Guid> TrackIds, string Title, string? Description)> CallGeminiAsync(
        string apiKey, string userPrompt, IReadOnlyList<CatalogTrackInfo> catalogTracks, CancellationToken cancellationToken)
    {
        var modelName = _configuration["Gemini:ModelName"] ?? "gemini-2.0-flash-lite";
        var catalogText = string.Join("\n", catalogTracks.Select(t => $"{t.Id} | {t.Title} | {t.ArtistName} | {t.Genre} | {t.DurationSeconds}s"));

        var requestBody = new GeminiRequest
        {
            SystemInstruction = new GeminiContent
            {
                Parts = new List<GeminiPart>
                {
                    new() { Text = "You select tracks from a catalog to build a playlist matching the user's request. " +
                                   "Reply ONLY with JSON: {\"title\": string, \"description\": string, \"trackIds\": string[]}. " +
                                   "trackIds must be a subset of the catalog IDs given, ordered as they should appear in the playlist. " +
                                   $"Catalog (id | title | artist | genre | durationSeconds):\n{catalogText}" }
                }
            },
            Contents = new List<GeminiContent>
            {
                new() { Parts = new List<GeminiPart> { new() { Text = userPrompt } } }
            },
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.7,
                MaxOutputTokens = 1024,
                ResponseMimeType = "application/json"
            }
        };

        var client = _httpClientFactory.CreateClient("gemini");
        var response = await client.PostAsJsonAsync($"v1beta/models/{modelName}:generateContent?key={apiKey}", requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: cancellationToken);
        var rawText = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(rawText))
            throw new InvalidOperationException("Empty response from Gemini.");

        var cleaned = Regex.Replace(rawText, "```json|```", "").Trim();
        var parsed = JsonSerializer.Deserialize<AiPlaylistResponse>(cleaned, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? throw new InvalidOperationException("Could not parse Gemini response.");

        var validIds = catalogTracks.Select(t => t.Id).ToHashSet();
        var trackIds = (parsed.TrackIds ?? new List<string>())
            .Where(id => Guid.TryParse(id, out var g) && validIds.Contains(g))
            .Select(id => Guid.Parse(id))
            .ToList();

        if (trackIds.Count == 0)
            throw new InvalidOperationException("Gemini returned no valid track IDs.");

        return (trackIds, string.IsNullOrWhiteSpace(parsed.Title) ? $"ШІ Мікс: {userPrompt}" : parsed.Title!, parsed.Description);
    }

    private static (List<Guid> TrackIds, string Title, string? Description) GenerateMockSelection(
        string userPrompt, IReadOnlyList<CatalogTrackInfo> catalogTracks)
    {
        var keywords = userPrompt
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = catalogTracks
            .Where(t => keywords.Any(k =>
                t.Title.ToLowerInvariant().Contains(k) ||
                t.Genre?.ToLowerInvariant().Contains(k) == true ||
                t.ArtistName.ToLowerInvariant().Contains(k)))
            .Select(t => t.Id)
            .Take(15)
            .ToList();

        if (matches.Count == 0)
        {
            matches = catalogTracks
                .OrderBy(_ => Random.Shared.Next())
                .Take(7)
                .Select(t => t.Id)
                .ToList();
        }

        var title = $"ШІ Мікс: {CapFirst(userPrompt)}";
        return (matches, title, "Автоматична підбірка на основі вашого запиту.");
    }

    private async Task<ServiceResult<PlaylistDto>> CreatePlaylistFromAiDataAsync(
        Guid userId, string title, string? description, List<Guid> orderedTrackIds, string baseUrl, CancellationToken cancellationToken)
    {
        var baseSlug = $"{PlaylistService.GenerateSlug(title)}-ai-mix";
        var uniqueSlug = baseSlug;
        int counter = 1;
        while (await _db.Playlists.IgnoreQueryFilters().AnyAsync(p => p.Slug == uniqueSlug, cancellationToken))
            uniqueSlug = $"{baseSlug}-{counter++}";

        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title.Trim(),
            Description = description,
            Slug = uniqueSlug,
            IsPrivate = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var tracks = await _db.Tracks
            .Where(t => orderedTrackIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ArtistName, t.DurationSeconds, t.IsExternal, t.ExternalCoverUrl, t.CoverImageRelativePath })
            .ToListAsync(cancellationToken);
        var trackById = tracks.ToDictionary(t => t.Id);

        int position = 1;
        double totalDuration = 0;
        var trackDtos = new List<PlaylistTrackDto>();
        foreach (var trackId in orderedTrackIds)
        {
            if (!trackById.TryGetValue(trackId, out var track)) continue;

            playlist.Tracks.Add(new PlaylistTrack { PlaylistId = playlist.Id, TrackId = trackId, Position = position, AddedAt = DateTime.UtcNow });
            totalDuration += track.DurationSeconds;

            var coverUrl = track.IsExternal
                ? track.ExternalCoverUrl
                : !string.IsNullOrWhiteSpace(track.CoverImageRelativePath)
                    ? $"{baseUrl}/music/files/{track.CoverImageRelativePath.Replace('\\', '/')}"
                    : null;

            trackDtos.Add(new PlaylistTrackDto(track.Id, track.Title, track.ArtistName, position, coverUrl, track.DurationSeconds));
            position++;
        }

        playlist.TrackCount = playlist.Tracks.Count;
        playlist.TotalDurationSeconds = (int)Math.Round(totalDuration);

        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync(cancellationToken);

        return ServiceResult<PlaylistDto>.Ok(new PlaylistDto(
            playlist.Id, playlist.UserId, playlist.Title, playlist.Description, playlist.Slug,
            playlist.CoverImageUrl, playlist.TrackCount, playlist.TotalDurationSeconds, playlist.IsPrivate,
            false, playlist.CreatedAt, trackDtos));
    }

    private static string CapFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private record CatalogTrackInfo(Guid Id, string Title, string ArtistName, string? Genre, double DurationSeconds);

    private class GeminiRequest
    {
        [JsonPropertyName("system_instruction")] public GeminiContent? SystemInstruction { get; set; }
        [JsonPropertyName("contents")] public List<GeminiContent> Contents { get; set; } = new();
        [JsonPropertyName("generationConfig")] public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    private class GeminiContent
    {
        [JsonPropertyName("parts")] public List<GeminiPart> Parts { get; set; } = new();
    }

    private class GeminiPart
    {
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }

    private class GeminiGenerationConfig
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; }
        [JsonPropertyName("responseMimeType")] public string? ResponseMimeType { get; set; }
    }

    private class GeminiResponse
    {
        [JsonPropertyName("candidates")] public List<GeminiCandidate>? Candidates { get; set; }
    }

    private class GeminiCandidate
    {
        [JsonPropertyName("content")] public GeminiContent? Content { get; set; }
    }

    private class AiPlaylistResponse
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public List<string>? TrackIds { get; set; }
    }
}
