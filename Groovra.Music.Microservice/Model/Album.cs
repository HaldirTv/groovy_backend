namespace Groovra.Music.Microservice.Model;

public class Album
{
    public Guid Id { get; set; }

    /// <summary>Власник альбому (артист).</summary>
    public Guid UserId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Кешоване ім'я артиста (щоб не ходити за юзером щоразу).</summary>
    public string ArtistName { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Відносний шлях до обкладинки альбому (наприклад "covers/{id}_album_cover.jpg").</summary>
    public string? CoverImageRelativePath { get; set; }

    public DateOnly? ReleaseDate { get; set; }

    /// <summary>Кешована кількість треків, оновлюється при Add/RemoveTrack.</summary>
    public int TrackCount { get; set; }

    /// <summary>Кешована сумарна тривалість треків у секундах.</summary>
    public double TotalDurationSeconds { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<Track> Tracks { get; set; } = new List<Track>();
}