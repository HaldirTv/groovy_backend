using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

public class UploadService
{

    private const long MaxAudioFileSizeBytes = 200L * 1024 * 1024;

    private const long MaxImageFileSizeBytes = 10L * 1024 * 1024;

    private static readonly HashSet<string> AllowedAudioMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg",        
        "audio/mp3",         
        "audio/mpeg3",       
        "audio/x-mpeg-3",    
        "audio/wav",         
        "audio/x-wav",       
        "audio/ogg",         
        "audio/flac",        
        "audio/x-flac",      
        "audio/aac",         
        "audio/x-m4a",       
        "audio/mp4",         
    };

    private static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    private readonly MusicDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UploadService> _logger;
    private readonly ICacheService _cache;

    private readonly string _mediaBasePath;

    public UploadService(MusicDbContext db, IConfiguration configuration, ICacheService cache, ILogger<UploadService> logger)
    {
        _db = db;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;

        var configured = _configuration["MediaStorage:BasePath"];
        var basePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Directory.GetCurrentDirectory(), "MediaStorage")
            : Path.GetFullPath(configured);

        try
        {
            Directory.CreateDirectory(Path.Combine(basePath, "audio"));
            Directory.CreateDirectory(Path.Combine(basePath, "covers"));
            _mediaBasePath = basePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create media path at {BasePath}, falling back to TempPath", basePath);
            _mediaBasePath = Path.Combine(Path.GetTempPath(), "MediaStorage");
            try
            {
                Directory.CreateDirectory(Path.Combine(_mediaBasePath, "audio"));
                Directory.CreateDirectory(Path.Combine(_mediaBasePath, "covers"));
            }
            catch { }
        }

    }

    public async Task<Track> UploadTrackAsync(
        UploadTrackRequestDto dto,
        Guid ownerUserId,   
        string artistName,  
        CancellationToken cancellationToken = default)
    {
        ValidateAudioFile(dto.File);

        if (dto.CoverImage is not null && dto.CoverImage.Length > 0)
        {
            ValidateCoverImage(dto.CoverImage);
        }

        var trackId = Guid.NewGuid();
        var audioExt = Path.GetExtension(dto.File.FileName).ToLowerInvariant();
        var audioFileName = $"{trackId}{audioExt}";
        var audioRelativePath = Path.Combine("audio", audioFileName);
        var audioAbsolutePath = Path.Combine(_mediaBasePath, audioRelativePath);

        await SaveFileAtomicAsync(dto.File, audioAbsolutePath, cancellationToken);

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

        var track = new Track
        {
            Id = trackId,
            UserId = ownerUserId,             
            Title = dto.Title.Trim(),
            ArtistName = artistName.Trim(),   
            AlbumTitle = string.IsNullOrWhiteSpace(dto.Album) ? null : dto.Album.Trim(),
            Genre = string.IsNullOrWhiteSpace(dto.Genre) ? null : dto.Genre.Trim(),
            Mood = string.IsNullOrWhiteSpace(dto.Mood) ? null : dto.Mood.Trim(),
            DurationSeconds = durationSeconds,
            FileSizeBytes = dto.File.Length,
            ContentType = dto.File.ContentType,
            AudioRelativePath = audioRelativePath,
            CoverImageRelativePath = coverRelativePath,
            UploadedAt = DateTime.UtcNow,
        };

        _db.Tracks.Add(track);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Трек збережено в БД. Id={TrackId}, Title={Title}, OwnerId={OwnerId}",
            track.Id, track.Title, track.UserId);

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

            track.AlbumId = album.Id;
            await _db.SaveChangesAsync(cancellationToken);
        }

        await _cache.RemoveAsync(CacheKeys.Genres, cancellationToken);
        foreach (var pattern in CacheKeys.ListPatterns)
        {
            await _cache.RemoveByPatternAsync(pattern, cancellationToken);
        }

        return track;
    }
    public async Task<string> UploadAlbumCoverAsync(IFormFile file, Guid albumId, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return string.Empty;

        if (file.Length > MaxImageFileSizeBytes)
            throw new ArgumentException($"Размер обложки превышает лимит ({MaxImageFileSizeBytes / (1024 * 1024)} MB).");

        if (!AllowedImageMimeTypes.Contains(file.ContentType))
            throw new ArgumentException($"Неподдерживаемый формат изображения '{file.ContentType}'.");

        var albumCoversDir = Path.Combine(_mediaBasePath, "albumcovers");
        try
        {
            if (!Directory.Exists(albumCoversDir))
            {
                Directory.CreateDirectory(albumCoversDir);
            }
        }
        catch { }

        var fileExtension = Path.GetExtension(file.FileName);
        var relativePath = Path.Combine("albumcovers", $"{albumId}_album_cover{fileExtension}").Replace('\\', '/');
        var absolutePath = Path.Combine(_mediaBasePath, relativePath);

        await SaveFileAtomicAsync(file, absolutePath, cancellationToken);

        return relativePath;
    }

    /// <summary>Видаляє медіафайл за відносним шляхом, якщо він існує. Використовується при
    /// заміні/видаленні обкладинки альбому, щоб не лишати осиротілі файли на диску.</summary>
    public void DeleteMediaFileIfExists(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        try
        {
            var absolutePath = Path.Combine(_mediaBasePath, relativePath.TrimStart('\\', '/'));
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Помилка при видаленні медіафайлу: {RelativePath}", relativePath);
        }
    }

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
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            throw;
        }

        File.Move(tempPath, destinationPath, overwrite: false);
    }
}
