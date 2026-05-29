namespace Groovra.Auth.Microservice.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;// можно сделать уникальным в будущем
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsPremium { get; set; } = false;
    public string Role { get; set; } = "Listener"; // Listener или Artist или Admin
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Навигационное свойство — если юзер стал артистом
    public Artist? ArtistProfile { get; set; }
}