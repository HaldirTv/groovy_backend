namespace Groovra.Auth.Microservice.Models;

public class OAuthRiskEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public string Provider { get; set; } = string.Empty; // e.g., "Google", "GitHub", "Microsoft"
    public int RiskScore { get; set; } // 0-100
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
