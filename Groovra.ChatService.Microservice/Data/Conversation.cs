namespace Groovra.ChatService.Microservice.Data;

public enum ConversationStatus
{
    Active = 0,
    Pending = 1
}

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsGroup { get; set; } = false;
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Заповнюється лише для групових бесід — завантажується так само, як медіа
    // повідомлень, через POST /api/media/upload, R2 не знає про різницю.
    public string? AvatarUrl { get; set; }

    // Одне закріплене повідомлення на бесіду (як у Telegram) — закріплення нового
    // просто перезаписує це поле. Без FK, той самий підхід, що й Message.SharedTrackId.
    public Guid? PinnedMessageId { get; set; }

    // Захист від спаму: щойно створена 1:1-бесіда (не знайдена через дедуп) стартує як
    // Pending, поки отримувач явно не натисне "Прийняти". Групи завжди Active.
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public Guid? RequestedByUserId { get; set; }

    public List<ConversationParticipant> Participants { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
}
