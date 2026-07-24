namespace Groovra.ChatService.Microservice.DTOS;

public record CreateConversationRequest(
    List<Guid> ParticipantUserIds,
    bool IsGroup = false,
    string? Title = null,
    string? AvatarUrl = null
);
