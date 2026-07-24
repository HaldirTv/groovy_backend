namespace Groovra.ChatService.Microservice.DTOS;

// MediaType — рядок "Image"/"Voice"/"File" (парситься в MessageType на бекенді),
// той самий підхід, що MessageDto.Type повертає назовні через ToString().
public record SendMediaMessageRequest(
    string MediaUrl,
    string MediaType,
    string? FileName,
    long? FileSizeBytes,
    Guid? ReplyToMessageId = null,
    Guid? ForwardedFromMessageId = null
);
