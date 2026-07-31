using Groovra.Auth.Microservice.Models;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Auth.Microservice.Data;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<Artist> Artists { get; set; }
    public DbSet<Role> Roles { get; set; }
    public DbSet<Profile> Profiles { get; set; }
    public DbSet<UserFollow> UserFollows { get; set; }

    // Адмінський аудит безпеки. Таблиці створюються ідемпотентним DDL у
    // MigrateDbHelper.EnsureColumns (як UserFollows/2FA/SettingsJson), а не через EF-міграцію -
    // знімок моделі вже розійшовся з реальною схемою спільної хмарної БД.
    public DbSet<LoginAudit> LoginAudits { get; set; }
    public DbSet<ThreatEvent> ThreatEvents { get; set; }
    public DbSet<OAuthRiskEvent> OAuthRiskEvents { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("auth");

        modelBuilder.Entity<UserFollow>()
            .ToTable("UserFollows")
            .HasKey(f => f.Id);

        modelBuilder.Entity<UserFollow>()
            .HasIndex(f => new { f.FollowerId, f.FollowedId })
            .IsUnique();

        modelBuilder.Entity<UserFollow>()
            .HasOne(f => f.Follower)
            .WithMany()
            .HasForeignKey(f => f.FollowerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<UserFollow>()
            .HasOne(f => f.Followed)
            .WithMany()
            .HasForeignKey(f => f.FollowedId)
            .OnDelete(DeleteBehavior.Restrict);

        // Аудит-таблиці: FK на Users навмисно Restrict (як у UserFollows) - історія входів і
        // подій безпеки не повинна зникати каскадом разом з користувачем. Індекси дублюють ті,
        // що створює MigrateDbHelper, щоб модель і фізична схема описували одне й те саме.
        modelBuilder.Entity<LoginAudit>(b =>
        {
            b.HasOne(l => l.User)
                .WithMany()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(l => l.Email).HasMaxLength(256);
            b.Property(l => l.IpAddress).HasMaxLength(64);
            b.Property(l => l.FailureReason).HasMaxLength(256);
            b.HasIndex(l => l.CreatedAt);
            b.HasIndex(l => new { l.Email, l.CreatedAt });
            b.HasIndex(l => l.UserId);
        });

        modelBuilder.Entity<ThreatEvent>(b =>
        {
            b.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(t => t.Type).HasMaxLength(64);
            b.Property(t => t.Description).HasMaxLength(1024);
            b.HasIndex(t => t.DetectedAt);
            b.HasIndex(t => t.Score);
            b.HasIndex(t => t.UserId);
        });

        modelBuilder.Entity<OAuthRiskEvent>(b =>
        {
            b.HasOne(o => o.User)
                .WithMany()
                .HasForeignKey(o => o.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            b.Property(o => o.Provider).HasMaxLength(64);
            b.Property(o => o.Reason).HasMaxLength(512);
            b.HasIndex(o => o.CreatedAt);
            b.HasIndex(o => o.Provider);
            b.HasIndex(o => o.UserId);
        });

        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<User>()
            .HasOne(u => u.ArtistProfile)
            .WithOne(a => a.User)
            .HasForeignKey<Artist>(a => a.UserId);

        modelBuilder.Entity<User>()
            .HasOne(u => u.Profile)
            .WithOne(p => p.User)
            .HasForeignKey<Profile>(p => p.UserId);

        modelBuilder.Entity<Profile>()
            .Property(p => p.Bio)
            .HasMaxLength(1000);

        modelBuilder.Entity<Artist>()
            .Property(a => a.Bio)
            .HasMaxLength(1000);
        modelBuilder.Entity<User>()
            .HasMany(u => u.Roles)
            .WithMany(r => r.Users)
            .UsingEntity(j => j.ToTable("UserRoles")); 
        modelBuilder.Entity<Role>().HasData(
            new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Listener" },
            new Role { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Artist" },
            new Role { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Admin" }
        );
    }
}