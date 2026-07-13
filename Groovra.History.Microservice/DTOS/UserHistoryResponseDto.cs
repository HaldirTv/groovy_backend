namespace Groovra.History.Microservice.DTOS;

public record UserHistoryResponseDto(
    Guid TrackId,
    DateTime PlayedAt
);