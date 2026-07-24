namespace Groovra.ChatService.Microservice.DTOS;

public record ShareTrackRequest(Guid TrackId, Guid? ReplyToMessageId = null, Guid? ForwardedFromMessageId = null);
