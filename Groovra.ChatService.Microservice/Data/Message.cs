namespace Groovra.ChatService.Microservice.Data;

public enum MessageType
{
    Text = 0,
    TrackShare = 1,
    Image = 2,
    Voice = 3,
    File = 4
}

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    public Guid SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;

    public MessageType Type { get; set; } = MessageType.Text;
    public string? Text { get; set; }

    public Guid? SharedTrackId { get; set; }

    public string? MediaUrl { get; set; }
    public string? MediaFileName { get; set; }
    public long? MediaFileSizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; } = false;

    public bool IsEdited { get; set; } = false;
    public DateTime? EditedAt { get; set; }

    public Guid? ReplyToMessageId { get; set; }

    public Guid? ForwardedFromMessageId { get; set; }
    public string? ForwardedFromSenderName { get; set; }
}
