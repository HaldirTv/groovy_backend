namespace Groovra.Music.Microservice.DTOs;

/// <summary>
/// Response returned after a successful track upload.
/// </summary>
public class UploadTrackResponseDto
{
    /// <summary>Unique identifier of the newly created track.</summary>
    public Guid TrackId { get; set; }

    /// <summary>Title of the uploaded track.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Artist name.</summary>
    public string ArtistName { get; set; } = string.Empty;

    /// <summary>Optional album name.</summary>
    public string? Album { get; set; }

    /// <summary>Genre tag.</summary>
    public string? Genre { get; set; }

    /// <summary>Mood/style tag.</summary>
    public string? Mood { get; set; }

    /// <summary>Duration of the audio in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Size of the original file in bytes.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>MIME content type of the stored file.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Relative URL to stream / download the audio.</summary>
    public string AudioUrl { get; set; } = string.Empty;

    /// <summary>Relative URL of the cover image (null if not provided).</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>UTC timestamp of the upload.</summary>
    public DateTime UploadedAt { get; set; }
}
