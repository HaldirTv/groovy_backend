using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Model;

public enum DownloadType
{
    Track,
    Playlist,
    Album
}

[Index(nameof(UserId), nameof(Type), nameof(ItemId), IsUnique = true)]
[Index(nameof(UserId), nameof(Type), nameof(AlbumName), nameof(ArtistName), IsUnique = true)]
public class Download
{
    [Key]
    public int Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    [Required]
    public DownloadType Type { get; set; }

    public Guid? ItemId { get; set; }

    [MaxLength(256)]
    public string? AlbumName { get; set; }

    [MaxLength(256)]
    public string? ArtistName { get; set; }

    public DateTime DownloadedAt { get; set; } = DateTime.UtcNow;
}