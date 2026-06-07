namespace Groovra.Auth.Microservice.Models;

public class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty; // "Listener", "Artist", "Admin"
    
    // Навигационное свойство для связи Многие-ко-многим
    public List<User> Users { get; set; } = new();
}