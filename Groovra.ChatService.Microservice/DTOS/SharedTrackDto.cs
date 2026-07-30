namespace Groovra.ChatService.Microservice.DTOS;

public record SharedTrackDto(
    Guid TrackId,
    string Title,
    string ArtistName,
    string? CoverImageUrl,
    string AudioUrl,
    double DurationSeconds
);
