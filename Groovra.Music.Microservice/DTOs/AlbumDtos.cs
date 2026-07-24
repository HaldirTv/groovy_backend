using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.DTOs;

// ─── Requests ──────────────────────────────────────────────────────────────


public class CreateAlbumDto
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public Guid? TargetUserId { get; set; }

    // Скаляр увидит это как массив или строку, потому что мы явно сказали [FromForm]
    [FromForm(Name = "TrackIds")]
    public List<Guid> TrackIds { get; set; } = new();

    public IFormFile? CoverFile { get; set; } 
}

public class UpdateAlbumDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    
    // Это для обновления обложки
    public IFormFile? CoverFile { get; set; } 
}

public class AddTracksToAlbumDto
{
    public List<Guid> TrackIds { get; set; } = new();
}

public class LikeAlbumRequestDto
{
    public Guid AlbumId { get; set; }
}

// ─── Responses ─────────────────────────────────────────────────────────────

public class AlbumTrackItemDto
{
    public Guid TrackId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string AudioUrl { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }
}

public class AlbumDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CoverImageUrl { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public int TrackCount { get; set; }
    public double TotalDurationSeconds { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsLiked { get; set; }
    public List<string> CollageCovers { get; set; } = new();
    public List<AlbumTrackItemDto> Tracks { get; set; } = new();
}

public class AlbumListItemDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }
    public int TrackCount { get; set; }
    public double TotalDurationSeconds { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public bool IsLiked { get; set; }
    public List<string> CollageCovers { get; set; } = new();
}