namespace Groovra.ChatService.Microservice.DTOS;

public record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string SenderName,
    string Type,
    string? Text,
    SharedTrackDto? Track,
    DateTime CreatedAt,
    bool IsEdited,
    DateTime? EditedAt,
    string? MediaUrl,
    string? MediaFileName,
    long? MediaFileSizeBytes,
    MessageReplyPreviewDto? ReplyTo,
    string? ForwardedFromSenderName
);
