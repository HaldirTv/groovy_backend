namespace Groovra.Auth.Microservice.Models;

public class Artist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Bio { get; set; } = string.Empty; 
    public string AvatarUrl { get; set; } = string.Empty;
    public string BannerUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}