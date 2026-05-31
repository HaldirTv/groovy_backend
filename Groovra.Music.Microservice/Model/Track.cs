using System.ComponentModel.DataAnnotations;

namespace Groovra.Music.Microservice.Model;

/// <summary>
/// EF Core сущность трека. Таблица живёт в схеме [music].[Tracks] базы GroovraDB.
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

    /// <summary>Длительность в секундах (0 = не определена).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Размер аудио-файла в байтах.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>MIME-тип аудио (audio/mpeg, audio/wav, …).</summary>
    [MaxLength(128)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Путь к аудио относительно MediaStorage (audio/&lt;guid&gt;.mp3).</summary>
    [MaxLength(512)]
    public string AudioRelativePath { get; set; } = string.Empty;

    /// <summary>Путь к обложке (null, если не загружена).</summary>
    [MaxLength(512)]
    public string? CoverImageRelativePath { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    [Required]
    public Guid UserId { get; set; }
}