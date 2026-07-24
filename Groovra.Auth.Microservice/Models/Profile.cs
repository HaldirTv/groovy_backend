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

        /// <summary>Вільний JSON-блоб для клієнтських налаштувань (тема, мова плеєра тощо) —
        /// без фіксованої серверної схеми, фронтенд сам вирішує, що там зберігати.</summary>
        public string SettingsJson { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        // ─── Заявка на статус артиста ───────────────────────────────────────
        // "None" | "Pending" | "Approved". Затвердження ролі Artist лишається
        // виключно дією адміна (AssignArtistRoleAsync/UsersController) — тут лише
        // фіксується сам факт та дані заявки.
        public string ArtistApplicationStatus { get; set; } = "None";
        public string ArtistApplicationName { get; set; } = string.Empty;
        public string ArtistApplicationGenre { get; set; } = string.Empty;
        public string ArtistApplicationCountry { get; set; } = string.Empty;
        public string ArtistApplicationPlatform { get; set; } = string.Empty;
        public DateTime? ArtistApplicationSubmittedAt { get; set; }
    }
}
