using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;

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
        "audio/mpeg",        // .mp3
        "audio/wav",         // .wav
        "audio/ogg",         // .ogg
        "audio/flac",        // .flac
        "audio/aac",         // .aac
        "audio/x-m4a",       // .m4a
        "audio/mp4",         // .m4a / .mp4 audio
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

    /// <summary>Absolute root path where media files will be stored.</summary>
    private readonly string _mediaBasePath;

    // ─── constructor ─────────────────────────────────────────────────────────

    public UploadService(MusicDbContext db, IConfiguration configuration, ILogger<UploadService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;

        // Read storage root from config; default to "MediaStorage" next to the executable.
        var configured = _configuration["MediaStorage:BasePath"];
        _mediaBasePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
            : Path.GetFullPath(configured);

        // Ensure the root directory (and sub-dirs) exist on startup.
        Directory.CreateDirectory(Path.Combine(_mediaBasePath, "audio"));
        Directory.CreateDirectory(Path.Combine(_mediaBasePath, "covers"));
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
        if (dto.CoverImage is not null)
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
        if (dto.CoverImage is not null)
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
            Album = string.IsNullOrWhiteSpace(dto.Album) ? null : dto.Album.Trim(),
            Genre = string.IsNullOrWhiteSpace(dto.Genre) ? null : dto.Genre.Trim(),
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

        return track;
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
