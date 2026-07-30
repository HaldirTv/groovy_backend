namespace Groovra.Auth.Microservice.DTOS;

public class ApplyArtistDto
{
    public string ArtistName { get; set; } = string.Empty;
    public string? Genre { get; set; }
    public string? Country { get; set; }
    public string? Platform { get; set; }
}

public class ArtistStatusResponseDto
{
    public bool IsArtist { get; set; }
    public string ApplicationStatus { get; set; } = "None";
    public string? ArtistName { get; set; }
    public string? Bio { get; set; }
    public DateTime? SubmittedAt { get; set; }
}
