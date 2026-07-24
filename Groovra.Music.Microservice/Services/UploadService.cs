using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

/// <summary>
/// Handles uploading and (later) management of audio media files.
/// Files are stored on the local file system under the path configured
/// in appsettings.json → "MediaStorage:BasePath".
/// 
/// Current implementation: Create (upload) only.
/// </summary>
public class UploadService
{
    // ─── constants ───────────────────────────────────────────────────────────

    /// <summary>Maximum allowed audio file size: 200 MB.</summary>
    private const long MaxAudioFileSizeBytes = 200L * 1024 * 1024;

    /// <summary>Maximum allowed cover image file size: 10 MB.</summary>
    private const long MaxImageFileSizeBytes = 10L * 1024 * 1024;

    /// <summary>Accepted audio MIME types.</summary>
    private static readonly HashSet<string> AllowedAudioMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg", "audio/mp3", "audio/mpeg3", "audio/x-mpeg-3", // .mp3 (разные браузеры/ОС отдают разные варианты)
        "audio/wav", "audio/x-wav",   // .wav
        "audio/ogg",                  // .ogg
        "audio/flac", "audio/x-flac", // .flac
        "audio/aac",                  // .aac
        "audio/x-m4a", "audio/mp4",   // .m4a / .mp4 audio
    };

    /// <summary>Accepted image MIME types for cover art.</summary>
    private static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    // ─── dependencies ────────────────────────────────────────────────────────

    private readonly MusicDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UploadService> _logger;
    private readonly ICacheService _cache;

    /// <summary>Absolute root path where media files will be stored.</summary>
    private readonly string _mediaBasePath;

    // ─── constructor ─────────────────────────────────────────────────────────

    public UploadService(MusicDbContext db, IConfiguration configuration, ILogger<UploadService> logger, ICacheService cache)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
        _cache = cache;

        // Read storage root from config; default to "MediaStorage" next to the executable.
        var configured = _configuration["MediaStorage:BasePath"];
        _mediaBasePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
            : Path.GetFullPath(configured);

        // Ensure the root directory (and sub-dirs) exist on startup.
        Directory.CreateDirectory(Path.Combine(_mediaBasePath, "audio"));
        Directory.CreateDirectory(Path.Combine(_mediaBasePath, "covers"));
    }

    /// <summary>Best-effort видалення файлу медіасховища за відносним шляхом — не кидає,
    /// якщо файлу вже нема. Використовується при остаточному (не soft) видаленні треків/
    /// альбомів/плейлистів користувачем, поза межами 30-денної джоби
    /// <see cref="GarbageCollectorService.CleanUpGarbageAsync"/>.</summary>
    public void DeleteRelativeFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;

        try
        {
            var absolutePath = Path.Combine(_mediaBasePath, relativePath.TrimStart('\\', '/'));
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
                _logger.LogDebug("Файл остаточно видалено з диска: {Path}", absolutePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при остаточному видаленні файлу: {RelativePath}", relativePath);
        }
    }

    // ─── public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads an audio file (and optional cover image) to the server's
    /// file system and returns a <see cref="Track"/> domain object describing
    /// the stored asset.
    /// </summary>
    /// <param name="dto">Validated request data from the controller.</param>
    /// <param name="cancellationToken">Cancellation token from the HTTP context.</param>
    /// <returns>The newly created <see cref="Track"/> record.</returns>
    /// <exception cref="ArgumentException">Thrown for invalid file type or size.</exception>
    /// <summary>
    /// Uploads an audio file (and optional cover image) to the server's
    /// file system and returns a <see cref="Track"/> domain object describing
    /// the stored asset.
    /// </summary>
    /// <param name="dto">Validated request data from the controller.</param>
    /// <param name="ownerUserId">ID пользователя, который загружает трек.</param>
    /// <param name="artistName">Финальное имя артиста (от шлюза или админа).</param>
    /// <param name="cancellationToken">Cancellation token from the HTTP context.</param>
    /// <returns>The newly created <see cref="Track"/> record.</returns>
    /// <exception cref="ArgumentException">Thrown for invalid file type or size.</exception>
    public async Task<Track> UploadTrackAsync(
        UploadTrackRequestDto dto,
        Guid ownerUserId,   
        string artistName,  
        CancellationToken cancellationToken = default)
    {
        // ── 1. Валідація аудіофайлу ────────────────────────────────────────
        ValidateAudioFile(dto.File);

        // ── 2. Валідація обкладинки (якщо є) ───────────────────────────────
        // Length > 0 отсекает случай, когда форма всегда шлёт поле CoverImage,
        // но пустым (без выбранного файла) — иначе ValidateCoverImage упадёт на пустом файле.
        if (dto.CoverImage is not null && dto.CoverImage.Length > 0)
        {
            ValidateCoverImage(dto.CoverImage);
        }

        // ── 3. Генерація унікального шляху збереження треку ─────────────────
        var trackId = Guid.NewGuid();
        var audioExt = Path.GetExtension(dto.File.FileName).ToLowerInvariant();
        var audioFileName = $"{trackId}{audioExt}";
        var audioRelativePath = Path.Combine("audio", audioFileName);
        var audioAbsolutePath = Path.Combine(_mediaBasePath, audioRelativePath);

        // ── 4. Атомарне збереження аудіофайлу ──────────────────────────────
        await SaveFileAtomicAsync(dto.File, audioAbsolutePath, cancellationToken);

        // ── 4.5. Читання тривалості аудіо за допомогою TagLib# ──────────────
        int durationSeconds = 0;
        try
        {
            using var tagFile = TagLib.File.Create(audioAbsolutePath);
            durationSeconds = (int)tagFile.Properties.Duration.TotalSeconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read audio duration for {File}: {Error}", 
                dto.File.FileName, ex.Message);
        }

        _logger.LogInformation(
            "Audio file saved. TrackId={TrackId}, File={FileName}, Size={Size} bytes, Duration={Duration}s",
            trackId, dto.File.FileName, dto.File.Length, durationSeconds);

        // ── 5. Збереження обкладинки (якщо є) ───────────────────────────────
        string? coverRelativePath = null;
        if (dto.CoverImage is not null && dto.CoverImage.Length > 0)
        {
            var coverExt = Path.GetExtension(dto.CoverImage.FileName).ToLowerInvariant();
            var coverFileName = $"{trackId}_cover{coverExt}";
            coverRelativePath = Path.Combine("covers", coverFileName);
            var coverAbsolutePath = Path.Combine(_mediaBasePath, coverRelativePath);

            await SaveFileAtomicAsync(dto.CoverImage, coverAbsolutePath, cancellationToken);

            _logger.LogInformation(
                "Cover image saved. TrackId={TrackId}, File={FileName}",
                trackId, dto.CoverImage.FileName);
        }

        // ── 6. Створення доменної моделі треку ──────────────────────────────
        var track = new Track
        {
            Id = trackId,
            UserId = ownerUserId,             
            Title = dto.Title.Trim(),
            ArtistName = artistName.Trim(),   
            AlbumTitle = string.IsNullOrWhiteSpace(dto.Album) ? null : dto.Album.Trim(),
            Genre = string.IsNullOrWhiteSpace(dto.Genre) ? null : dto.Genre.Trim(),
            Mood = string.IsNullOrWhiteSpace(dto.Mood) ? null : dto.Mood.Trim(),
            DurationSeconds = durationSeconds, // <--- Використовуємо реальну тривалість
            FileSizeBytes = dto.File.Length,
            ContentType = dto.File.ContentType,
            AudioRelativePath = audioRelativePath,
            CoverImageRelativePath = coverRelativePath,
            UploadedAt = DateTime.UtcNow,
        };

        // ── 7. Збереження в базу даних ──────────────────────────────────────
        _db.Tracks.Add(track);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Трек збережено в БД. Id={TrackId}, Title={Title}, OwnerId={OwnerId}",
            track.Id, track.Title, track.UserId);

        // If an album title was provided with the upload, attach the track to an existing album
        // or create a new album for this user. This mirrors common services behaviour where
        // providing an album name groups tracks automatically.
        if (!string.IsNullOrWhiteSpace(track.AlbumTitle))
        {
            var albumTitle = track.AlbumTitle!.Trim();
            var album = await _db.Albums
                .FirstOrDefaultAsync(a => a.UserId == ownerUserId && a.Title == albumTitle && !a.IsDeleted, cancellationToken);

            if (album is null)
            {
                album = new Album
                {
                    Id = Guid.NewGuid(),
                    UserId = ownerUserId,
                    Title = albumTitle,
                    ArtistName = artistName,
                    Description = null,
                    ReleaseDate = null,
                    TrackCount = 1,
                    TotalDurationSeconds = track.DurationSeconds,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                _db.Albums.Add(album);
                _logger.LogInformation("Created album '{Title}' (Id={Id}) for user {UserId}", albumTitle, album.Id, ownerUserId);
            }
            else
            {
                album.TrackCount++;
                album.TotalDurationSeconds += track.DurationSeconds;
                album.UpdatedAt = DateTime.UtcNow;
            }

            // Link the track to the album and save changes
            track.AlbumId = album.Id;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Новый трек попадает в поиск/тренды/рекомендации и, возможно, в список жанров —
        // сбрасываем связанные кеши списков, чтобы он не "потерялся" до истечения TTL.
        await _cache.RemoveAsync(CacheKeys.Genres, cancellationToken);
        foreach (var pattern in CacheKeys.ListPatterns)
        {
            await _cache.RemoveByPatternAsync(pattern, cancellationToken);
        }

        return track;
    }
    
    
    /// <summary>
    /// Проверяет и атомарно сохраняет обложку альбома в папку MediaStorage/albumcovers
    /// </summary>
    public async Task<string> UploadAlbumCoverAsync(IFormFile file, Guid albumId, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return string.Empty;

        // Используем ваш лимит на изображения (10 MB)
        if (file.Length > MaxImageFileSizeBytes)
            throw new ArgumentException($"Размер обложки превышает лимит ({MaxImageFileSizeBytes / (1024 * 1024)} MB).");

        // Используем ваш белый список контент-типов (jpeg, png, webp)
        if (!AllowedImageMimeTypes.Contains(file.ContentType))
            throw new ArgumentException($"Неподдерживаемый формат изображения '{file.ContentType}'.");

        var albumCoversDir = Path.Combine(_mediaBasePath, "albumcovers");
        if (!Directory.Exists(albumCoversDir))
        {
            Directory.CreateDirectory(albumCoversDir);
        }

        var fileExtension = Path.GetExtension(file.FileName);
        var relativePath = Path.Combine("albumcovers", $"{albumId}_album_cover{fileExtension}").Replace('\\', '/');
        var absolutePath = Path.Combine(_mediaBasePath, relativePath);

        await SaveFileAtomicAsync(file, absolutePath, cancellationToken);

        return relativePath;
    }
    
    
    
    
    
    // ─── private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Validates that the audio IFormFile meets size and MIME type requirements.
    /// </summary>
    private static void ValidateAudioFile(IFormFile file)
    {
        if (file.Length == 0)
            throw new ArgumentException("Audio file is empty.", nameof(file));

        if (file.Length > MaxAudioFileSizeBytes)
            throw new ArgumentException(
                $"Audio file exceeds the maximum allowed size of {MaxAudioFileSizeBytes / (1024 * 1024)} MB.",
                nameof(file));

        if (!AllowedAudioMimeTypes.Contains(file.ContentType))
            throw new ArgumentException($"Unsupported audio format '{file.ContentType}'.", nameof(file));
    }

    /// <summary>
    /// Validates that the cover image IFormFile meets size and MIME type requirements.
    /// </summary>
    private static void ValidateCoverImage(IFormFile file)
    {
        if (file.Length == 0)
            throw new ArgumentException("Cover image file is empty.", nameof(file));

        if (file.Length > MaxImageFileSizeBytes)
            throw new ArgumentException(
                $"Cover image exceeds the maximum allowed size of {MaxImageFileSizeBytes / (1024 * 1024)} MB.",
                nameof(file));

        if (!AllowedImageMimeTypes.Contains(file.ContentType))
            throw new ArgumentException($"Unsupported image format '{file.ContentType}'.", nameof(file));
    }

    /// <summary>
    /// Saves an <see cref="IFormFile"/> atomically by first writing to a temporary
    /// file and then moving it to the final destination.  This prevents partial/
    /// corrupt files being visible to other readers during the write.
    /// </summary>
    private static async Task SaveFileAtomicAsync(
        IFormFile file,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var tempPath = destinationPath + ".tmp";

        try
        {
            await using var tempStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                useAsync: true);

            await file.CopyToAsync(tempStream, cancellationToken);
        }
        catch
        {
            // Clean up the temp file if something goes wrong.
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            throw;
        }

        // Atomic move (same volume → rename, which is O(1) on most OSes).
        File.Move(tempPath, destinationPath, overwrite: false);
    }
}
