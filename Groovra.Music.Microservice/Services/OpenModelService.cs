using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

public class OpenModelService
{
    private readonly MusicDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenModelService> _logger;

    public OpenModelService(
        MusicDbContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpenModelService> logger)
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
        _logger.LogInformation("Generating AI Mix for user {UserId} with prompt: '{Prompt}'", userId, userPrompt);

        var apiKey = _configuration["OpenModel:ApiKey"];
        var isMockMode = string.IsNullOrWhiteSpace(apiKey) || 
                         apiKey.Contains("YOUR_OPENMODEL_API_KEY") || 
                         apiKey.Equals("mock", StringComparison.OrdinalIgnoreCase) || 
                         apiKey.Equals("dummy", StringComparison.OrdinalIgnoreCase);

        if (isMockMode)
        {
            _logger.LogInformation("OpenModel API Key not configured or set to mock. Running in fallback Mock Mode.");
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
            {
                return ServiceResult<PlaylistDto>.Fail("Не знайдено доступних треків у базі даних.");
            }

            var trackListBuilder = new StringBuilder();
            foreach (var track in dbTracks)
            {
                trackListBuilder.AppendLine($"- ID: {track.Id}, Title: \"{track.Title}\", Artist: \"{track.ArtistName}\", Genre: \"{track.Genre}\", Duration: {track.DurationSeconds}s");
            }

            var modelName = _configuration["OpenModel:ModelName"] ?? "deepseek-v4-flash";
            var systemMessage = "You are a professional AI DJ for 'Groovra' music streaming service. The user wants a custom playlist. Select the best tracks matching the user request. You must respond ONLY with a JSON object in this format:\n" +
                                "{\n" +
                                "  \"title\": \"Cool playlist title (in Ukrainian/English depending on prompt language)\",\n" +
                                "  \"description\": \"Brief creative description (in Ukrainian/English) explaining why these tracks fit\",\n" +
                                "  \"trackIds\": [\"guid1\", \"guid2\", ...]\n" +
                                "}\n" +
                                "Return raw JSON. Do not include markdown like ```json wrapper.";

            var userMessage = $"User prompt: {userPrompt}\n\nAvailable tracks:\n{trackListBuilder.ToString()}";

            var isResponsesProtocol = modelName.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase);
            var endpoint = isResponsesProtocol ? "responses" : "messages";
            object requestBody;

            if (isResponsesProtocol)
            {
                requestBody = new
                {
                    model = modelName,
                    messages = new[]
                    {
                        new { role = "system", content = systemMessage },
                        new { role = "user", content = userMessage }
                    },
                    temperature = 0.7,
                    response_format = new { type = "json_object" }
                };
            }
            else
            {
                requestBody = new
                {
                    model = modelName,
                    system = systemMessage,
                    messages = new[]
                    {
                        new { role = "user", content = userMessage }
                    },
                    max_tokens = 4096,
                    temperature = 0.7
                };
            }

            var client = _httpClientFactory.CreateClient("openmodel");
            var response = await client.PostAsJsonAsync(endpoint, requestBody, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("OpenModel API call failed. Status: {Status}, Details: {Details}", response.StatusCode, errorText);
                _logger.LogWarning("Falling back to Mock mode due to API error.");
                return await GenerateMockMixAsync(userId, userPrompt, baseUrl, cancellationToken);
            }

            string? jsonString = null;
            if (isResponsesProtocol)
            {
                var apiResult = await response.Content.ReadFromJsonAsync<OpenModelChatResponse>(cancellationToken: cancellationToken);
                jsonString = apiResult?.Choices?.FirstOrDefault()?.Message?.Content;
            }
            else
            {
                var apiResult = await response.Content.ReadFromJsonAsync<OpenModelMessageResponse>(cancellationToken: cancellationToken);
                jsonString = apiResult?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
            }

            if (string.IsNullOrWhiteSpace(jsonString))
            {
                _logger.LogWarning("OpenModel API returned empty content. Falling back to Mock mode.");
                return await GenerateMockMixAsync(userId, userPrompt, baseUrl, cancellationToken);
            }

            jsonString = jsonString.Trim();
            if (jsonString.StartsWith("```"))
            {
                var match = Regex.Match(jsonString, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    jsonString = match.Groups[1].Value.Trim();
                }
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var aiResponse = JsonSerializer.Deserialize<OpenModelPlaylistResponse>(jsonString, options);

            if (aiResponse == null || string.IsNullOrWhiteSpace(aiResponse.Title) || aiResponse.TrackIds == null || !aiResponse.TrackIds.Any())
            {
                _logger.LogWarning("OpenModel JSON parsing resulted in empty or invalid structure. Falling back to Mock mode.");
                return await GenerateMockMixAsync(userId, userPrompt, baseUrl, cancellationToken);
            }

            return await CreatePlaylistFromGeneratedDataAsync(userId, aiResponse.Title, aiResponse.Description, aiResponse.TrackIds, baseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during OpenModel AI playlist generation. Falling back to Mock mode.");
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
        {
            return ServiceResult<PlaylistDto>.Fail("Не знайдено доступних треків у базі даних.");
        }

        var promptWords = userPrompt.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);

        var selectedTracks = dbTracks.Where(t =>
            promptWords.Any(w =>
                (t.Title?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Genre?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.ArtistName?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)
            )
        ).Take(10).ToList();

        if (!selectedTracks.Any())
        {
            var random = new Random();
            selectedTracks = dbTracks.OrderBy(t => random.Next()).Take(7).ToList();
        }

        var title = $"ШІ Мікс: {CapitalizeFirstLetter(userPrompt)}";
        var description = $"Створено за допомогою Groovra AI Core (Mock Mode) для запиту: '{userPrompt}'. Містить {selectedTracks.Count} треків.";
        var trackIds = selectedTracks.Select(t => t.Id.ToString()).ToList();

        return await CreatePlaylistFromGeneratedDataAsync(userId, title, description, trackIds, baseUrl, cancellationToken);
    }

    private async Task<ServiceResult<PlaylistDto>> CreatePlaylistFromGeneratedDataAsync(
        Guid userId,
        string title,
        string description,
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
        {
            return ServiceResult<PlaylistDto>.Fail("Не вдалося підібрати існуючі треки для цього міксу.");
        }

        var baseSlug = GenerateSlug(title);
        var uniqueSlug = baseSlug;
        int counter = 1;
        while (await _context.Playlists.IgnoreQueryFilters()
                   .AnyAsync(p => p.Slug == uniqueSlug, cancellationToken))
        {
            uniqueSlug = $"{baseSlug}-{counter++}";
        }

        var playlist = new Playlist
        {
            Id          = Guid.NewGuid(),
            UserId      = userId,
            Title       = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            IsPrivate   = false,
            Slug        = uniqueSlug,
            TrackCount  = orderedTracks.Count,
            TotalDurationSeconds = (int)orderedTracks.Sum(t => Math.Round(t!.DurationSeconds)),
            IsDeleted   = false,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow,
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

        _logger.LogInformation("AI Generated Playlist saved. Id={Id}, Title={Title}", playlist.Id, playlist.Title);

        var playlistDto = new PlaylistDto(
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
            playlist.Tracks.Select(pt => new PlaylistTrackDto(
                pt.TrackId,
                orderedTracks.First(t => t!.Id == pt.TrackId)!.Title,
                orderedTracks.First(t => t!.Id == pt.TrackId)!.ArtistName,
                pt.Position,
                orderedTracks.First(t => t!.Id == pt.TrackId)!.IsExternal
                    ? orderedTracks.First(t => t!.Id == pt.TrackId)!.ExternalCoverUrl
                    : orderedTracks.First(t => t!.Id == pt.TrackId)!.CoverImageRelativePath != null
                        ? $"{baseUrl}/music/files/{orderedTracks.First(t => t!.Id == pt.TrackId)!.CoverImageRelativePath!.Replace('\\', '/')}"
                        : null,
                orderedTracks.First(t => t!.Id == pt.TrackId)!.DurationSeconds
            )).ToList()
        );

        return ServiceResult<PlaylistDto>.Ok(playlistDto);
    }

    private static string CapitalizeFirstLetter(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return str;
        return char.ToUpper(str[0]) + str.Substring(1);
    }

    private static string GenerateSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "ai-mix";
        var str = title.ToLowerInvariant().Trim();
        str = Transliterate(str);
        str = Regex.Replace(str, @"[^a-z0-9\s-]", "");
        str = Regex.Replace(str, @"\s+", " ").Replace(" ", "-");
        str = Regex.Replace(str, @"-+", "-");
        return str.Trim('-');
    }

    private static string Transliterate(string text)
    {
        string[] cyr = { "а","б","в","г","д","е","є","ж","з","и","і","ї","й","к","л","м","н","о","п","р","с","т","у","ф","х","ц","ч","ш","щ","ь","ю","я" };
        string[] lat = { "a","b","v","g","d","e","ye","zh","z","y","i","yi","y","k","l","m","n","o","p","r","s","t","u","f","kh","ts","ch","sh","shch","","yu","ya" };
        for (int i = 0; i < cyr.Length; i++) text = text.Replace(cyr[i], lat[i]);
        return text.Replace("ё","yo").Replace("ъ","").Replace("ы","y").Replace("э","e");
    }

    private class OpenModelChatResponse
    {
        public List<OpenModelChoice>? Choices { get; set; }
    }

    private class OpenModelChoice
    {
        public OpenModelMessage? Message { get; set; }
    }

    private class OpenModelMessage
    {
        public string? Content { get; set; }
    }

    private class OpenModelMessageResponse
    {
        public List<OpenModelContentItem>? Content { get; set; }
    }

    private class OpenModelContentItem
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
    }

    private class OpenModelPlaylistResponse
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> TrackIds { get; set; } = new();
    }
}
