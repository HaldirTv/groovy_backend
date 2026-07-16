using System;
using System.Collections.Generic;

namespace Groovra.Music.Microservice.DTOs;

public record CreatePlaylistDto(
    string Title,
    string? Description,
    bool IsPrivate = false
);

public record UpdatePlaylistPrivacyDto(bool IsPrivate);

public record AddTrackToPlaylistDto(Guid TrackId);

public record ReorderTracksDto(IList<Guid> OrderedTrackIds);

public record PlaylistDto(
    Guid Id,
    Guid UserId,
    string Title,
    string? Description,
    string Slug,
    string? CoverImageUrl,
    int TrackCount,
    double TotalDurationSeconds,
    bool IsPrivate,
    DateTime CreatedAt,
    IList<PlaylistTrackDto> Tracks
);

public record PlaylistTrackDto(
    Guid TrackId,
    string Title,
    string ArtistName,
    int Position,
    string? CoverUrl,
    double DurationSeconds
);


public class PlaylistListItemDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsPrivate { get; set; }
    public string Slug { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public double TotalDurationSeconds { get; set; }
    public string? CoverImageUrl { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<string> CollageCovers { get; set; } = new();
}