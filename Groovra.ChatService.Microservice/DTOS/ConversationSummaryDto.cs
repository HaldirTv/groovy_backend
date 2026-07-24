namespace Groovra.ChatService.Microservice.DTOS;

public record ConversationSummaryDto(
    Guid Id,
    bool IsGroup,
    string? Title,
    List<ParticipantDto> Participants,
    string? LastMessagePreview,
    DateTime? LastMessageAt,
    DateTime CreatedAt,
    Guid? PinnedMessageId,
    string? AvatarUrl,
    string Status,
    Guid? RequestedByUserId
);
