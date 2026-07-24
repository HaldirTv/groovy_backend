namespace Groovra.ChatService.Microservice.DTOS;

public record ConversationDto(
    Guid Id,
    bool IsGroup,
    string? Title,
    List<ParticipantDto> Participants,
    DateTime CreatedAt,
    Guid? PinnedMessageId,
    MessageReplyPreviewDto? PinnedMessage,
    string? AvatarUrl,
    string Status,
    Guid? RequestedByUserId
);
