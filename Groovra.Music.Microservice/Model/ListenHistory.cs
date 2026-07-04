namespace Groovra.Music.Microservice.Model;

public class ListenHistory
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public Guid TrackId { get; set; }

    public DateTime ListenedAt { get; set; } = DateTime.UtcNow;


    public double ProgressSeconds { get; set; } 
    public bool Completed { get; set; }

    public DateTime? CompletedAt { get; set; }
    

}