namespace Groovra.Auth.Microservice.Models;

public class ThreatEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public string Type { get; set; } = string.Empty; // e.g., "BruteForce", "SuspiciousLogin", "GeoAnomaly"
    public int Score { get; set; } // 0-100
    public string Description { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public bool IsResolved { get; set; } = false;
}
