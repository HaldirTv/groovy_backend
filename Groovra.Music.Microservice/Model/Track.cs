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

    /// <summary>Категорія настрою/стилю для секції рекомендацій (напр. "Chill", "Workout") —
    /// вільний текст за тим самим принципом, що й Genre, виставляється лише при завантаженні.</summary>
    [MaxLength(64)]
    public string? Mood { get; set; }

    /// <summary>Длительность в секундах (0 = не определена).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Размер аудио-файла в байтах.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>MIME-тип аудио (audio/mpeg, audio/wav, …).</summary>
    [MaxLength(128)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Флаг: загружен ли трек на наш сервер вручную или взят из внешнего API (Jamendo).</summary>
    public bool IsExternal { get; set; } = false;

    /// <summary>Прямой URL на внешний аудиопоток (заполняется только если IsExternal = true).</summary>
    [MaxLength(1024)]
    public string? ExternalAudioUrl { get; set; }

    /// <summary>Прямой URL на внешнюю обложку (заполняется только если IsExternal = true).</summary>
    [MaxLength(1024)]
    public string? ExternalCoverUrl { get; set; }

    /// <summary>Путь к аудио относительно MediaStorage (null для внешних треков).</summary>
    [MaxLength(512)]
    public string? AudioRelativePath { get; set; } // Сделали nullable (?)

    /// <summary>Путь к обложке относительно MediaStorage (null для внешних треков).</summary>
    [MaxLength(512)]
    public string? CoverImageRelativePath { get; set; } // Сделали nullable (?)

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    [Required]
    public Guid UserId { get; set; } // Для треков Jamendo можно зашить Guid системного администратора/бота

    public long PlayCount { get; set; } = 0;
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    /// <summary>Синхронизированный текст песни в формате LRC. Null = ещё не искали,
    /// пустая строка = уже искали и подтвердили, что трек инструментальный (кэш "не найдено").</summary>
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
