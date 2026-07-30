using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Groovra.Music.Microservice.Model;

public class Track
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string ArtistName { get; set; } = string.Empty;

    public string? AlbumTitle { get; set; }   
    public Guid? AlbumId { get; set; }
    public Album? Album { get; set; } 

    [MaxLength(128)]
    public string? Genre { get; set; }

    [MaxLength(64)]
    public string? Mood { get; set; }

    public double DurationSeconds { get; set; }

    public long FileSizeBytes { get; set; }

    [MaxLength(128)]
    public string ContentType { get; set; } = string.Empty;

    public bool IsExternal { get; set; } = false;

    [MaxLength(1024)]
    public string? ExternalAudioUrl { get; set; }

    [MaxLength(1024)]
    public string? ExternalCoverUrl { get; set; }

    [MaxLength(512)]
    public string? AudioRelativePath { get; set; } 

    [MaxLength(512)]
    public string? CoverImageRelativePath { get; set; } 

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    [Required]
    public Guid UserId { get; set; } 

    public long PlayCount { get; set; } = 0;
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    public string? LyricsLrc { get; set; }
    [NotMapped]
    public string? CoverImageUrl
    {
        get
        {
            if (IsExternal) 
                return ExternalCoverUrl;

            if (!string.IsNullOrWhiteSpace(CoverImageRelativePath))
                return $"/music/files/{CoverImageRelativePath.Replace('\\', '/')}";

            return null;
        }
    }
}
