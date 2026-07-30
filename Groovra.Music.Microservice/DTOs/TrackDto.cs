namespace Groovra.Music.Microservice.DTOs;

public class TrackDto
{
    public Guid TrackId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ArtistName { get; set; } = string.Empty;

    public string? Album { get; set; }

    public string? Genre { get; set; }

    public string? Mood { get; set; }

    public double DurationSeconds { get; set; }

    public long FileSizeBytes { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public string AudioUrl { get; set; } = string.Empty;

    public string? CoverImageUrl { get; set; }

    public DateTime UploadedAt { get; set; }

    public long PlayCount { get; set; }
    public bool IsLiked { get; set; }
}