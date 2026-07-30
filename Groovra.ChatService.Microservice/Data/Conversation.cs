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

    public string? AvatarUrl { get; set; }

    public Guid? PinnedMessageId { get; set; }

    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public Guid? RequestedByUserId { get; set; }

    public List<ConversationParticipant> Participants { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
}
