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

    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public ParticipantRole Role { get; set; } = ParticipantRole.Member;

    public DateTime? ClearedAt { get; set; }
}
