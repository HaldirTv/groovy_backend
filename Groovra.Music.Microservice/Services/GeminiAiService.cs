using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Shared.ServiceResult;

namespace Groovra.Music.Microservice.Services;

public class GeminiAiService
{
    private readonly MusicDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiAiService> _logger;

    public GeminiAiService(
        MusicDbContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GeminiAiService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ServiceResult<PlaylistDto>> GenerateAiMixAsync(
        Guid userId,
        string userPrompt,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating AI Mix via Gemini for user {UserId}, prompt: '{Prompt}'", userId, userPrompt);

        var apiKey = _configuration["Gemini:ApiKey"];
        var isMockMode = string.IsNullOrWhiteSpace(apiKey) ||
                         apiKey.Contains("YOUR_GEMINI") ||
                         apiKey.Equals("mock", StringComparison.OrdinalIgnoreCase);

        if (isMockMode)
        {
            _logger.LogWarning("Gemini API key not configured — running in mock mode.");
            return await GenerateMockMixAsync(userId, userPrompt, baseUrl, cancellationToken);
        }

        try
        {
            var dbTracks = await _context.Tracks
                .AsNoTracking()
                .Where(t => !t.IsDeleted)
                .OrderByDescending(t => t.PlayCount)
                .Take(60)
                .ToListAsync(cancellationToken);

            if (!dbTracks.Any())
                return ServiceResult<PlaylistDto>.Fail("Не знайдено доступних треків у базі даних.");

            var catalogBuilder = new StringBuilder();
            foreach (var t in dbTracks)
                catalogBuilder.AppendLine($"- ID: {t.Id} | Title: \"{t.Title}\" | Artist: \"{t.ArtistName}\" | Genre: \"{t.Genre}\" | Duration: {t.DurationSeconds}s");

            var modelName = _configuration["Gemini:ModelName"] ?? "gemini-2.0-flash-lite";

            var systemInstruction = "You are a professional AI DJ for 'Groovra' music streaming service. " +
                                    "Select the best tracks from the catalog that match the user request. " +
                                    "Respond ONLY with a raw JSON object — no markdown, no code fences. Format:\n" +
                                    "{\n" +
                                    "  \"title\": \"Playlist title (match language of user prompt)\",\n" +
                                    "  \"description\": \"Brief creative description of the playlist\",\n" +
                                    "  \"trackIds\": [\"guid1\", \"guid2\", ...]\n" +
                                    "}\n" +
                                    "Choose 5-15 tracks. Return raw JSON only.";

            var userMessage = $"User request: {userPrompt}\n\nAvailable tracks catalog:\n{catalogBuilder}";

            var requestBody = new GeminiRequest
            {
                SystemInstruction = new GeminiContent
                {
                    Parts = new[] { new GeminiPart { Text = systemInstruction } }
                },
                Contents = new[]
                {
                    new GeminiContent
                    {
                        Role = "user",
                        Parts = new[] { new GeminiPart { Text = userMessage } }
                    }
                },
                GenerationConfig = new GeminiGenerationConfig
                {
                    Temperature = 0.7f,
                    MaxOutputTokens = 1024,
                    ResponseMimeType = "application/json"
                }
            };

            var client = _httpClientFactory.CreateClient("gemini");
            var endpoint = $"v1beta/models/{modelName}:generateContent?key={apiKey}";

            var httpResponse = await client.PostAsJsonAsync(endpoint, requestBody, cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorText = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Gemini API error. Status: {Status}, Body: {Body}", httpResponse.StatusCode, errorText);
                return await GenerateMockMixAsync(userId, userPrompt, baseUrl, cancellationToken);
            }

            var geminiResponse = await httpResponse.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: cancellationToken);
            var jsonText = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(jsonText))
            {
                _logger.LogWarning("Gemini returned empty content. Falling back to mock.");
                return await GenerateMockMixAsync(userId, userPrompt, baseUrl, cancellationToken);
            }

            jsonText = StripMarkdownJson(jsonText.Trim());

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var aiResponse = JsonSerializer.Deserialize<AiPlaylistResponse>(jsonText, options);

            if (aiResponse == null || string.IsNullOrWhiteSpace(aiResponse.Title) || !aiResponse.TrackIds.Any())
            {
                _logger.LogWarning("Gemini response parsing failed. Falling back to mock.");
                return await GenerateMockMixAsync(userId, userPrompt, baseUrl, cancellationToken);
            }

            return await CreatePlaylistFromAiDataAsync(userId, aiResponse.Title, aiResponse.Description, aiResponse.TrackIds, baseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini AI generation failed. Falling back to mock.");
            return await GenerateMockMixAsync(userId, userPrompt, baseUrl, cancellationToken);
        }
    }

    private async Task<ServiceResult<PlaylistDto>> GenerateMockMixAsync(
        Guid userId,
        string userPrompt,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var dbTracks = await _context.Tracks
            .AsNoTracking()
            .Where(t => !t.IsDeleted)
            .ToListAsync(cancellationToken);

        if (!dbTracks.Any())
            return ServiceResult<PlaylistDto>.Fail("Не знайдено доступних треків у базі даних.");

        var promptWords = userPrompt.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);

        var selected = dbTracks.Where(t =>
            promptWords.Any(w =>
                (t.Title?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Genre?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.ArtistName?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
            )
        ).Take(10).ToList();

        if (!selected.Any())
            selected = dbTracks.OrderBy(_ => Random.Shared.Next()).Take(7).ToList();

        var title = $"ШІ Мікс: {CapFirst(userPrompt)}";
        var description = $"Персональний мікс від Groovra AI для запиту: '{userPrompt}'.";
        var ids = selected.Select(t => t.Id.ToString()).ToList();

        return await CreatePlaylistFromAiDataAsync(userId, title, description, ids, baseUrl, cancellationToken);
    }

    private async Task<ServiceResult<PlaylistDto>> CreatePlaylistFromAiDataAsync(
        Guid userId,
        string title,
        string? description,
        List<string> trackIds,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var guids = trackIds
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        var existingTracks = await _context.Tracks
            .Where(t => !t.IsDeleted && guids.Contains(t.Id))
            .ToListAsync(cancellationToken);

        var orderedTracks = guids
            .Select(g => existingTracks.FirstOrDefault(t => t.Id == g))
            .Where(t => t != null)
            .ToList();

        if (!orderedTracks.Any())
            return ServiceResult<PlaylistDto>.Fail("Не вдалося підібрати існуючі треки для цього міксу.");

        var slug = await MakeUniqueSlugAsync(GenerateSlug(title), cancellationToken);

        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            IsPrivate = false,
            Slug = slug,
            TrackCount = orderedTracks.Count,
            TotalDurationSeconds = (int)orderedTracks.Sum(t => Math.Round(t!.DurationSeconds)),
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        for (int i = 0; i < orderedTracks.Count; i++)
        {
            playlist.Tracks.Add(new PlaylistTrack
            {
                PlaylistId = playlist.Id,
                TrackId = orderedTracks[i]!.Id,
                Position = i + 1,
                AddedAt = DateTime.UtcNow
            });
        }

        _context.Playlists.Add(playlist);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("AI playlist saved. Id={Id}, Title={Title}", playlist.Id, playlist.Title);

        var dto = new PlaylistDto(
            playlist.Id,
            playlist.UserId,
            playlist.Title,
            playlist.Description,
            playlist.Slug,
            playlist.CoverImageUrl,
            playlist.TrackCount,
            playlist.TotalDurationSeconds,
            playlist.IsPrivate,
            false,
            playlist.CreatedAt,
            playlist.Tracks.Select(pt =>
            {
                var track = orderedTracks.First(t => t!.Id == pt.TrackId)!;
                var coverUrl = track.IsExternal
                    ? track.ExternalCoverUrl
                    : track.CoverImageRelativePath != null
                        ? $"{baseUrl}/music/files/{track.CoverImageRelativePath.Replace('\\', '/')}"
                        : null;
                return new PlaylistTrackDto(pt.TrackId, track.Title, track.ArtistName, pt.Position, coverUrl, track.DurationSeconds);
            }).ToList()
        );

        return ServiceResult<PlaylistDto>.Ok(dto);
    }

    private async Task<string> MakeUniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        var counter = 1;
        while (await _context.Playlists.IgnoreQueryFilters().AnyAsync(p => p.Slug == slug, ct))
            slug = $"{baseSlug}-{counter++}";
        return slug;
    }

    private static string StripMarkdownJson(string text)
    {
        var match = Regex.Match(text, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : text;
    }

    private static string CapFirst(string s) =>
        string.IsNullOrWhiteSpace(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static string GenerateSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "ai-mix";
        var s = Transliterate(title.ToLowerInvariant().Trim());
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"\s+", " ").Replace(" ", "-");
        s = Regex.Replace(s, @"-+", "-");
        return s.Trim('-');
    }

    private static string Transliterate(string text)
    {
        string[] cyr = { "а","б","в","г","д","е","є","ж","з","и","і","ї","й","к","л","м","н","о","п","р","с","т","у","ф","х","ц","ч","ш","щ","ь","ю","я" };
        string[] lat = { "a","b","v","g","d","e","ye","zh","z","y","i","yi","y","k","l","m","n","o","p","r","s","t","u","f","kh","ts","ch","sh","shch","","yu","ya" };
        for (int i = 0; i < cyr.Length; i++) text = text.Replace(cyr[i], lat[i]);
        return text.Replace("ё","yo").Replace("ъ","").Replace("ы","y").Replace("э","e");
    }

    private class GeminiRequest
    {
        [JsonPropertyName("system_instruction")]
        public GeminiContent? SystemInstruction { get; set; }

        [JsonPropertyName("contents")]
        public GeminiContent[]? Contents { get; set; }

        [JsonPropertyName("generationConfig")]
        public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    private class GeminiContent
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("parts")]
        public GeminiPart[]? Parts { get; set; }
    }

    private class GeminiPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class GeminiGenerationConfig
    {
        [JsonPropertyName("temperature")]
        public float Temperature { get; set; }

        [JsonPropertyName("maxOutputTokens")]
        public int MaxOutputTokens { get; set; }

        [JsonPropertyName("responseMimeType")]
        public string? ResponseMimeType { get; set; }
    }

    private class GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public List<GeminiCandidate>? Candidates { get; set; }
    }

    private class GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; set; }
    }

    private class AiPlaylistResponse
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> TrackIds { get; set; } = new();
    }
}
