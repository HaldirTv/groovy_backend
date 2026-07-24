namespace Groovra.ChatService.Microservice.DTOS;

// Мінімальний "знімок" треку для вбудовування у повідомлення — повні дані
// (як TrackDto в Music) не потрібні, Chat лише показує картку в чаті.
public record SharedTrackDto(
    Guid TrackId,
    string Title,
    string ArtistName,
    string? CoverImageUrl,
    string AudioUrl,
    double DurationSeconds
);
