namespace Groovra.History.Microservice.Data;

public class PlaybackHistory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TrackId { get; set; }
    public DateTime PlayedAt { get; set; }
}