namespace Groovra.ChatService.Microservice.DTOS;

public record ParticipantDto(Guid UserId, string UserName, string Role);
