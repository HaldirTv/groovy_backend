namespace Groovra.ChatService.Microservice.Data;

// Чорний список — той самий денормалізований підхід без FK на Auth, що і
// ConversationParticipant.UserId. Один рядок = BlockerUserId заблокував BlockedUserId.
public class BlockedUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BlockerUserId { get; set; }
    public Guid BlockedUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
