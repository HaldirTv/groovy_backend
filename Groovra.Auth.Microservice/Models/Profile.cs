namespace Groovra.Auth.Microservice.Models
{
    public class Profile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
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
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
    }
}
