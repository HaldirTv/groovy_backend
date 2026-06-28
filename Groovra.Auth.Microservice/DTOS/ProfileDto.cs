namespace Groovra.Auth.Microservice.DTOS;
public class ProfileResponseDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Birthday { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string BannerUrl { get; set; } = string.Empty;
    public string LinkUrl { get; set; } = string.Empty;
    public string LinkLabel { get; set; } = string.Empty;
    public string SupportLink { get; set; } = string.Empty;
}

public class UpdateProfileDto
{
    public string? DisplayName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Bio { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Phone { get; set; }
    public string? Birthday { get; set; }
    public string? Gender { get; set; }
    public string? LinkUrl { get; set; }
    public string? LinkLabel { get; set; }
    public string? SupportLink { get; set; }
}


