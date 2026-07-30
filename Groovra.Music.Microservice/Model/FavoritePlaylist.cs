namespace Groovra.Music.Microservice.Model;

public class FavoritePlaylist
{
    public Guid UserId { get; set; }
    public Guid PlaylistId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Playlist? Playlist { get; set; }
}