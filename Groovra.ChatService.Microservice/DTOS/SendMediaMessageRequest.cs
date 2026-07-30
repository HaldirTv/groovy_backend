namespace Groovra.ChatService.Microservice.DTOS;

public record SendMediaMessageRequest(
    string MediaUrl,
    string MediaType,
    string? FileName,
    long? FileSizeBytes,
    Guid? ReplyToMessageId = null,
    Guid? ForwardedFromMessageId = null
);
