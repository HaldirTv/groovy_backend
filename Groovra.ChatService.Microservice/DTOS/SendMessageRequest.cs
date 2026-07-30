namespace Groovra.ChatService.Microservice.DTOS;

public record SendMessageRequest(string Text, Guid? ReplyToMessageId = null, Guid? ForwardedFromMessageId = null);
