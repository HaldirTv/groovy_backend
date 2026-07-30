namespace Groovra.Messaging.Contracts;

public record TrackPlayedEvent(
    Guid UserId, 
    Guid TrackId, 
    DateTime PlayedAt
);