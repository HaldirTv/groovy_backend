namespace Groovra.ChatService.Microservice.Data;

// "Видалено для мене" — повідомлення лишається для решти учасників,
// але ховається для конкретного юзера. Один рядок = одне приховане повідомлення
// для одного юзера (унікальний індекс на (MessageId, UserId) в ChatDbContext).
public class MessageDeletion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public Message Message { get; set; } = null!;

    // Без FK — той самий підхід, що ConversationParticipant.UserId застосовує до Auth.
    public Guid UserId { get; set; }

    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
}
