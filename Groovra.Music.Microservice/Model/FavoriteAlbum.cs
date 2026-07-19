namespace Groovra.Music.Microservice.Model;

public class FavoriteAlbum
{
    public Guid UserId { get; set; }
    public Guid AlbumId { get; set; }
 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
 
    public Album? Album { get; set; }
}