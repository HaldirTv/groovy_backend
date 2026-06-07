namespace Groovra.Auth.Microservice.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
 
  
    public List<Role> Roles { get; set; } = new(); 
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
 
    public Artist? ArtistProfile { get; set; }
}