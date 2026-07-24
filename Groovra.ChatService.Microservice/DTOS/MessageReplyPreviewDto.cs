namespace Groovra.ChatService.Microservice.DTOS;

// Коротке прев'ю повідомлення для відображення в "Message.ReplyTo" і
// "ConversationDto.PinnedMessage" — не повний MessageDto, лише те, що потрібно
// показати в UI (уникаємо рекурсії/зайвого гідрування треків для прев'ю).
public record MessageReplyPreviewDto(
    Guid MessageId,
    Guid SenderId,
    string SenderName,
    string Type,
    string? TextSnippet,
    string? MediaFileName
);
