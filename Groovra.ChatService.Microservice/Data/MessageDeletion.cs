namespace Groovra.ChatService.Microservice.Data;

public class MessageDeletion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public Message Message { get; set; } = null!;

    public Guid UserId { get; set; }

    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
}
