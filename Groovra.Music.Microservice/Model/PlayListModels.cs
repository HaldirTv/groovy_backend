namespace Groovra.Music.Microservice.Model;

/// <summary>
/// Represents a user-created playlist.
/// </summary>
public class Playlist
{
    public Guid   Id          { get; set; } = Guid.NewGuid();
    public Guid   UserId      { get; set; } // Идентификатор из X-User-Id от API Gateway

    public string  Title       { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Человекочитаемый уникальный идентификатор для URL (например, chill-vibes-2026)
    /// </summary>
    public string? Slug { get; set; }

    /// <summary>
    /// URL или путь к обложке плейлиста
    /// </summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// Денормализованные счётчики
    /// </summary>
    public int TrackCount { get; set; } = 0;
    public int TotalDurationSeconds { get; set; } = 0;

    /// <summary>
    /// Флаги мягкого удаления
    /// </summary>
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    public bool IsPrivate { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<PlaylistTrack> Tracks { get; set; } = new List<PlaylistTrack>();
}

/// <summary>
/// Join entity linking a Playlist to a Track, with an explicit ordering position.
/// </summary>
public class PlaylistTrack
{
    public Guid PlaylistId { get; set; }
    public Guid TrackId    { get; set; }

    /// <summary>1-based display order within the playlist.</summary>
    public int Position { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Playlist? Playlist { get; set; }
    public Track?    Track    { get; set; }
}