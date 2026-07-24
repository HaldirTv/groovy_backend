using Groovra.Music.Microservice.Model;

namespace Groovra.Music.Microservice.Caching;

/// <summary>
/// Сериализуемая проекция <see cref="Track"/> для Redis. Без навигационного свойства
/// Album — запросы, которые мы кешируем (GetAllTracksAsync/GetPopularTracksAsync/
/// GetMoodRecommendationsAsync) его и так не подгружают через Include, поэтому
/// AlbumTitle уже несёт нужное значение.
/// </summary>
public sealed class CachedTrack
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string? AlbumTitle { get; set; }
    public string? Genre { get; set; }
    public string? Mood { get; set; }
    public double DurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public bool IsExternal { get; set; }
    public string? ExternalAudioUrl { get; set; }
    public string? ExternalCoverUrl { get; set; }
    public string? CoverImageRelativePath { get; set; }
    public DateTime UploadedAt { get; set; }
    public long PlayCount { get; set; }

    public static CachedTrack FromTrack(Track track) => new()
    {
        Id = track.Id,
        Title = track.Title,
        ArtistName = track.ArtistName,
        AlbumTitle = track.AlbumTitle ?? track.Album?.Title,
        Genre = track.Genre,
        Mood = track.Mood,
        DurationSeconds = track.DurationSeconds,
        FileSizeBytes = track.FileSizeBytes,
        ContentType = track.ContentType,
        IsExternal = track.IsExternal,
        ExternalAudioUrl = track.ExternalAudioUrl,
        ExternalCoverUrl = track.ExternalCoverUrl,
        CoverImageRelativePath = track.CoverImageRelativePath,
        UploadedAt = track.UploadedAt,
        PlayCount = track.PlayCount,
    };

    /// <summary>Восстанавливает лёгкий <see cref="Track"/> из кеша — ровно с тем набором
    /// полей, которые нужны существующему TracksController.MapToDto, чтобы не трогать
    /// саму логику маппинга (IsLiked/baseUrl остаются per-request).</summary>
    public Track ToTrack() => new()
    {
        Id = Id,
        Title = Title,
        ArtistName = ArtistName,
        AlbumTitle = AlbumTitle,
        Genre = Genre,
        Mood = Mood,
        DurationSeconds = DurationSeconds,
        FileSizeBytes = FileSizeBytes,
        ContentType = ContentType,
        IsExternal = IsExternal,
        ExternalAudioUrl = ExternalAudioUrl,
        ExternalCoverUrl = ExternalCoverUrl,
        CoverImageRelativePath = CoverImageRelativePath,
        UploadedAt = UploadedAt,
        PlayCount = PlayCount,
    };
}

public sealed class CachedTrackPage
{
    public List<CachedTrack> Items { get; set; } = [];
    public int TotalCount { get; set; }
}

public sealed class CachedMoodGroup
{
    public string Mood { get; set; } = string.Empty;
    public List<CachedTrack> Tracks { get; set; } = [];
}
