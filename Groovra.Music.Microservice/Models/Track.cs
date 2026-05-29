using System.ComponentModel.DataAnnotations;

namespace Groovra.Music.Microservice.Models;

/// <summary>
/// Domain model representing an uploaded audio track stored on disk.
/// This is a lightweight "file-system-first" model; a proper DB-backed
/// model (with EF Core) can be layered on top later.
/// </summary>
public class Track
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string ArtistName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Album { get; set; }

    [MaxLength(128)]
    public string? Genre { get; set; }

    /// <summary>Duration extracted from the file header (seconds).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Size of the audio file in bytes.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>MIME type of the stored audio (e.g. audio/mpeg).</summary>
    [MaxLength(128)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Path on disk relative to the media root (e.g. "audio/2024/abc.mp3").</summary>
    [MaxLength(512)]
    public string AudioRelativePath { get; set; } = string.Empty;

    /// <summary>Optional cover image relative path.</summary>
    [MaxLength(512)]
    public string? CoverImageRelativePath { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
