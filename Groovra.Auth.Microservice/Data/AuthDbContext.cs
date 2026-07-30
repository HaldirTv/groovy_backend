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