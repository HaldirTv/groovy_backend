namespace Groovra.ChatService.Microservice.Data;

public class BlockedUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BlockerUserId { get; set; }
    public Guid BlockedUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
