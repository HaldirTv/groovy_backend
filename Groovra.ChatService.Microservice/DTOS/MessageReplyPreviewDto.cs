namespace Groovra.ChatService.Microservice.DTOS;

public record MessageReplyPreviewDto(
    Guid MessageId,
    Guid SenderId,
    string SenderName,
    string Type,
    string? TextSnippet,
    string? MediaFileName
);
