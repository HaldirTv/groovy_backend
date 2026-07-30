using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;
using Whisper.net;
using Whisper.net.Ggml;

namespace Groovra.Music.Microservice.Services;

public class LyricsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LyricsService> _logger;
    private readonly string _modelPath;
    private readonly string _mediaBasePath;
    private WhisperFactory? _whisperFactory;
    private bool _modelReady = false;

    private const string LrclibBaseUrl = "https://lrclib.net/api";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin";

    public LyricsService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<LyricsService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var basePathConfig = configuration["MediaStorage:BasePath"];
        _mediaBasePath = string.IsNullOrWhiteSpace(basePathConfig)
            ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
            : Path.GetFullPath(basePathConfig);

        _modelPath = Path.Combine(_mediaBasePath, "models", "ggml-tiny.bin");
    }

    public async Task InitializeAsync()
    {
        try
        {
            var modelsDir = Path.GetDirectoryName(_modelPath)!;
            try { Directory.CreateDirectory(modelsDir); } catch { }

            if (!File.Exists(_modelPath))
            {
                _logger.LogInformation("LyricsService: модель Whisper не найдена. Загрузка ggml-tiny.bin (~75 MB)...");
                await DownloadModelAsync();
            }
            else
            {
                _logger.LogInformation("LyricsService: модель Whisper найдена по пути {Path}", _modelPath);
            }

            try
            {
                _whisperFactory = WhisperFactory.FromPath(_modelPath, delayInitialization: true);
                _modelReady = true;
                _logger.LogInformation("LyricsService: Whisper.net готов к работе.");
            }
            catch (Exception whisperEx)
            {
                _logger.LogWarning(whisperEx, "LyricsService: Whisper.net native init failed (whisper_log_set). AI-fallback будет недоступен.");
                _modelReady = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LyricsService: критическая ошибка при инициализации. AI-fallback будет недоступен.");
            _modelReady = false;
        }
    }

    public async Task<string?> GetOrCreateLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track.LyricsLrc is not null)
        {
            _logger.LogInformation("LyricsService: лирика для '{Title}' найдена в кэше БД.", track.Title);
            return track.LyricsLrc;
        }

        _logger.LogInformation("LyricsService: поиск в LRCLIB для '{Artist} - {Title}'.", track.ArtistName, track.Title);
        var lrclibResult = await TryFetchFromLrclibAsync(track, cancellationToken);
        if (lrclibResult is not null)
        {
            _logger.LogInformation("LyricsService: лирика найдена в LRCLIB.");
            await CacheLyricsAsync(track.Id, lrclibResult);
            return lrclibResult;
        }

        if (_modelReady && _whisperFactory is not null)
        {
            _logger.LogInformation("LyricsService: LRCLIB не нашёл текст, запуск Whisper AI для '{Title}'.", track.Title);
            var whisperResult = await TranscribeWithWhisperAsync(track, cancellationToken);
            if (whisperResult is not null)
            {
                await CacheLyricsAsync(track.Id, whisperResult);
                return whisperResult;
            }
        }

        _logger.LogWarning("LyricsService: не удалось получить текст для '{Title}'. Кэшируем как инструментальный.", track.Title);
        await CacheLyricsAsync(track.Id, string.Empty);
        return string.Empty;
    }

    private async Task<string?> TryFetchFromLrclibAsync(Track track, CancellationToken cancellationToken)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("lrclib");
            var albumName = track.AlbumTitle ?? track.Album?.Title;
            var url = $"{LrclibBaseUrl}/get?artist_name={Uri.EscapeDataString(track.ArtistName)}" +
                      $"&track_name={Uri.EscapeDataString(track.Title)}" +
                      (!string.IsNullOrWhiteSpace(albumName) ? $"&album_name={Uri.EscapeDataString(albumName)}" : "") +
                      $"&duration={(int)track.DurationSeconds}";

            var response = await http.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<LrclibResponseDto>(cancellationToken: cancellationToken);
                if (dto?.SyncedLyrics is not null && dto.SyncedLyrics.Length > 10)
                    return dto.SyncedLyrics;
                if (dto?.Instrumental == true)
                    return string.Empty;
                if (dto?.PlainLyrics is not null && dto.PlainLyrics.Length > 10)
                    return FormatPlainLyricsAsLrc(dto.PlainLyrics, track.DurationSeconds);
            }

            var searchUrl = $"{LrclibBaseUrl}/get?artist_name={Uri.EscapeDataString(track.ArtistName)}" +
                            $"&track_name={Uri.EscapeDataString(track.Title)}";

            var searchResponse = await http.GetAsync(searchUrl, cancellationToken);
            if (searchResponse.IsSuccessStatusCode)
            {
                var dto = await searchResponse.Content.ReadFromJsonAsync<LrclibResponseDto>(cancellationToken: cancellationToken);
                if (dto?.SyncedLyrics is not null && dto.SyncedLyrics.Length > 10)
                    return dto.SyncedLyrics;
                if (dto?.PlainLyrics is not null && dto.PlainLyrics.Length > 10)
                    return FormatPlainLyricsAsLrc(dto.PlainLyrics, track.DurationSeconds);
            }

            var combinedQuery = $"{track.ArtistName} {track.Title}".Trim();
            var searchApiUrl = $"{LrclibBaseUrl}/search?q={Uri.EscapeDataString(combinedQuery)}";
            var queryResponse = await http.GetAsync(searchApiUrl, cancellationToken);
            if (queryResponse.IsSuccessStatusCode)
            {
                var searchList = await queryResponse.Content.ReadFromJsonAsync<List<LrclibResponseDto>>(cancellationToken: cancellationToken);
                if (searchList != null && searchList.Count > 0)
                {
                    var itemWithSynced = searchList.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SyncedLyrics) && x.SyncedLyrics.Length > 10);
                    if (itemWithSynced?.SyncedLyrics != null)
                        return itemWithSynced.SyncedLyrics;

                    var itemWithPlain = searchList.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.PlainLyrics) && x.PlainLyrics.Length > 10);
                    if (itemWithPlain?.PlainLyrics != null)
                        return FormatPlainLyricsAsLrc(itemWithPlain.PlainLyrics, track.DurationSeconds);
                }
            }

            if (!string.IsNullOrWhiteSpace(track.Title) && track.Title != combinedQuery)
            {
                var titleSearchUrl = $"{LrclibBaseUrl}/search?q={Uri.EscapeDataString(track.Title)}";
                var titleResponse = await http.GetAsync(titleSearchUrl, cancellationToken);
                if (titleResponse.IsSuccessStatusCode)
                {
                    var searchList = await titleResponse.Content.ReadFromJsonAsync<List<LrclibResponseDto>>(cancellationToken: cancellationToken);
                    if (searchList != null && searchList.Count > 0)
                    {
                        var itemWithSynced = searchList.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SyncedLyrics) && x.SyncedLyrics.Length > 10);
                        if (itemWithSynced?.SyncedLyrics != null)
                            return itemWithSynced.SyncedLyrics;

                        var itemWithPlain = searchList.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.PlainLyrics) && x.PlainLyrics.Length > 10);
                        if (itemWithPlain?.PlainLyrics != null)
                            return FormatPlainLyricsAsLrc(itemWithPlain.PlainLyrics, track.DurationSeconds);
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LyricsService: ошибка запроса к LRCLIB для '{Title}'.", track.Title);
            return null;
        }
    }

    private static string FormatPlainLyricsAsLrc(string plainLyrics, double durationSeconds)
    {
        var lines = plainLyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return string.Empty;

        var step = durationSeconds > 0 ? durationSeconds / lines.Length : 3.0;
        var lrcBuilder = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var currentSec = i * step;
            var minutes = (int)(currentSec / 60);
            var seconds = (int)(currentSec % 60);
            var hundredths = (int)((currentSec - Math.Floor(currentSec)) * 100);
            lrcBuilder.AppendLine($"[{minutes:00}:{seconds:00}.{hundredths:00}] {lines[i]}");
        }
        return lrcBuilder.ToString();
    }

    private async Task<string?> TranscribeWithWhisperAsync(Track track, CancellationToken cancellationToken)
    {
        if (track.IsExternal || string.IsNullOrWhiteSpace(track.AudioRelativePath))
        {
            _logger.LogInformation("LyricsService: трек {TrackId} внешний/без локального файла, Whisper пропущен.", track.Id);
            return null;
        }

        var audioPath = Path.Combine(_mediaBasePath, track.AudioRelativePath);
        if (!File.Exists(audioPath))
        {
            _logger.LogWarning("LyricsService: аудиофайл не найден: {Path}", audioPath);
            return null;
        }

        var wavPath = Path.ChangeExtension(Path.GetTempFileName(), ".wav");
        try
        {
            var ffmpegArgs = $"-y -i \"{audioPath}\" -ar 16000 -ac 1 -f wav \"{wavPath}\"";
            var exitCode = await RunProcessAsync("ffmpeg", ffmpegArgs, cancellationToken);
            if (exitCode != 0 || !File.Exists(wavPath))
            {
                _logger.LogWarning("LyricsService: ffmpeg конвертация не удалась для '{Title}'.", track.Title);
                return null;
            }

            using var processor = _whisperFactory!.CreateBuilder()
                .WithLanguageDetection()
                .Build();

            var lrcBuilder = new StringBuilder();
            await using var fileStream = File.OpenRead(wavPath);

            await foreach (var segment in processor.ProcessAsync(fileStream, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(segment.Text)) continue;
                var start = segment.Start;
                var minutes = (int)start.TotalMinutes;
                var seconds = start.Seconds;
                var hundredths = start.Milliseconds / 10;
                lrcBuilder.AppendLine($"[{minutes:00}:{seconds:00}.{hundredths:00}] {segment.Text.Trim()}");
            }

            return lrcBuilder.Length > 0 ? lrcBuilder.ToString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LyricsService: ошибка транскрибирования для '{Title}'.", track.Title);
            return null;
        }
        finally
        {
            if (File.Exists(wavPath)) File.Delete(wavPath);
        }
    }

    private static async Task<int> RunProcessAsync(string executable, string args, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private async Task CacheLyricsAsync(Guid trackId, string lrc)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
            await db.Tracks
                .Where(t => t.Id == trackId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LyricsLrc, lrc));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LyricsService: не удалось кэшировать текст для трека {Id}.", trackId);
        }
    }

    private async Task DownloadModelAsync()
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(_modelPath);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;
            if (total > 0)
            {
                var pct = (int)(downloaded * 100 / total);
                if (downloaded % (1024 * 1024 * 5) < 81920)
                    _logger.LogInformation("LyricsService: загрузка модели {Pct}% ({MB} MB)...", pct, downloaded / 1024 / 1024);
            }
        }

        _logger.LogInformation("LyricsService: модель загружена ({MB} MB).", downloaded / 1024 / 1024);
    }

    private class LrclibResponseDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("instrumental")]
        public bool Instrumental { get; init; }
    }
}
