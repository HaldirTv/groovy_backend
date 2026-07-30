using System.ComponentModel.DataAnnotations;

namespace Groovra.Music.Microservice.DTOs;

public class RenameTrackRequestDto
{
    [Required]
    [MinLength(1)]
    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;
}
