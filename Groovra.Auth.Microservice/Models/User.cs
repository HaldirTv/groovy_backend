using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(50)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    [EmailAddress(ErrorMessage = "Невірний формат email.")]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)] 
    public string PasswordHash { get; set; } = string.Empty;
    public List<Role> Roles { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Виставляється адміністратором (admin/users/{id}/suspend). Заблокований
    /// користувач не може залогінитись — перевірка у ReglogService.ValidateUserForLoginAsync
    /// та LoginOrRegisterGoogleUserAsync, плюс усі його сесії відкликаються одразу.</summary>
    public bool IsSuspended { get; set; } = false;

    public bool TwoFactorEnabled { get; set; } = false;
    public string? TwoFactorSecret { get; set; }
    public string? TempTwoFactorSecret { get; set; }
    public string? TwoFactorRecoveryCodesJson { get; set; }
    public Artist? ArtistProfile { get; set; }

    public Profile? Profile { get; set; }
}