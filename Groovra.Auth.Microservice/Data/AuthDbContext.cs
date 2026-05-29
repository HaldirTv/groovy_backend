using Groovra.Auth.Microservice.Models;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Auth.Microservice.Data;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<Artist> Artists { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("auth");
        // Унікальний email
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();
        
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();


        // Один юзер — один профіль артиста
        modelBuilder.Entity<User>()
            .HasOne(u => u.ArtistProfile)
            .WithOne(a => a.User)
            .HasForeignKey<Artist>(a => a.UserId);

        // Обмеження Bio до 1000 символів
        modelBuilder.Entity<Artist>()
            .Property(a => a.Bio)
            .HasMaxLength(1000);
    }
}