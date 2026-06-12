namespace Groovra.Music.Microservice.DTOs;

/// <summary>
/// Ответный DTO для отображения трека (GET /music/tracks и GET /music/tracks/{id}).
/// </summary>
public class TrackDto
{
    /// <summary>Уникальный идентификатор трека.</summary>
    public Guid TrackId { get; set; }

    /// <summary>Название трека.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Имя исполнителя.</summary>
    public string ArtistName { get; set; } = string.Empty;

    /// <summary>Альбом (может быть null).</summary>
    public string? Album { get; set; }

    /// <summary>Жанр (может быть null).</summary>
    public string? Genre { get; set; }

    /// <summary>Длительность в секундах.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Размер файла в байтах.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>MIME-тип аудио.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>URL для стриминга / скачивания аудио.</summary>
    public string AudioUrl { get; set; } = string.Empty;

    /// <summary>URL обложки (null, если не загружена).</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>Дата и время загрузки (UTC).</summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>Количество прослушиваний.</summary>
    public long PlayCount { get; set; }
}