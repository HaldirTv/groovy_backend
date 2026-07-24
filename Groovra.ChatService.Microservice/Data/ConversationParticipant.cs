namespace Groovra.ChatService.Microservice.Data;

public enum ParticipantRole
{
    Member = 0,
    Admin = 1
}

public class ConversationParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    // Groovra.Auth "власник" даних юзера — Chat навмисно не має FK на нього (інша БД-схема,
    // той самий підхід, що History застосовує до Music: тримаємо лише Guid).
    public Guid UserId { get; set; }

    // Денормалізоване ім'я на момент приєднання до бесіди — той самий прийом,
    // що TrackComment.AuthorName використовує в Music, щоб не ходити за іменем щоразу.
    public string UserName { get; set; } = string.Empty;

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    // Має сенс лише для групових бесід (Conversation.IsGroup) — творець групи отримує
    // Admin, усі інші — Member. Для 1:1-бесід завжди Member, ніде не перевіряється.
    public ParticipantRole Role { get; set; } = ParticipantRole.Member;

    // Проставляється при "очистити для мене" — повідомлення, надіслані до цього моменту,
    // більше не показуються цьому учаснику, але лишаються для решти учасників.
    public DateTime? ClearedAt { get; set; }
}
