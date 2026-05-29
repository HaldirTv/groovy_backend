using System.ComponentModel.DataAnnotations;

namespace Groovra.Music.Microservice.DTOs;

/// <summary>
/// Тело запроса для PATCH /music/tracks/{id}/title.
/// </summary>
public class RenameTrackRequestDto
{
    /// <summary>Новое название трека (обязательно, 1–256 символов).</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;
}
