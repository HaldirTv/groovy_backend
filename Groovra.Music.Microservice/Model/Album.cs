namespace Groovra.Music.Microservice.Model;

public class Album
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ArtistName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? CoverImageRelativePath { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    public int TrackCount { get; set; }

    public double TotalDurationSeconds { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<Track> Tracks { get; set; } = new List<Track>();
}