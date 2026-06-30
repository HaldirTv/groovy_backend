using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Model;

[Index(nameof(UserId), nameof(TrackId), IsUnique = true)]
public class FavoriteTrack
{
    [Key]
    public int Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    [Required]
    public Guid TrackId { get; set; }

    // Навигационное свойство, чтобы EF Core мог красиво подтягивать всю инфу о треке через .Include()
    [ForeignKey(nameof(TrackId))]
    public Track? Track { get; set; }

    public DateTime LikedAt { get; set; } = DateTime.UtcNow;
}