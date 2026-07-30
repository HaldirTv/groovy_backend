namespace Groovra.Music.Microservice.Model;

public class Playlist
{
    public Guid   Id          { get; set; } = Guid.NewGuid();
    public Guid   UserId      { get; set; } 

    public string  Title       { get; set; } = string.Empty;
    public string? Description { get; set; }

    public string? Slug { get; set; }

    public string? CoverImageUrl { get; set; }

    public int TrackCount { get; set; } = 0;
    public int TotalDurationSeconds { get; set; } = 0;

    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    public bool IsPrivate { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PlaylistTrack> Tracks { get; set; } = new List<PlaylistTrack>();
}

public class PlaylistTrack
{
    public Guid PlaylistId { get; set; }
    public Guid TrackId    { get; set; }

    public int Position { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public Playlist? Playlist { get; set; }
    public Track?    Track    { get; set; }
}