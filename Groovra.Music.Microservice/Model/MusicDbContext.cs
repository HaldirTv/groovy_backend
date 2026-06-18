using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Model;

public class MusicDbContext : DbContext
{
    public MusicDbContext(DbContextOptions<MusicDbContext> options) : base(options) { }

    public DbSet<Track> Tracks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Все таблицы Music-сервиса живут в схеме [music] базы GroovraDB
        modelBuilder.HasDefaultSchema("music");

        modelBuilder.Entity<Track>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Title).IsRequired().HasMaxLength(256);
            entity.Property(t => t.ArtistName).IsRequired().HasMaxLength(256);
            entity.Property(t => t.Album).HasMaxLength(256);
            entity.Property(t => t.Genre).HasMaxLength(128);
            entity.Property(t => t.ContentType).HasMaxLength(128);
            

            entity.Property(t => t.AudioRelativePath).HasMaxLength(512); 
            entity.Property(t => t.CoverImageRelativePath).HasMaxLength(512);
            
            // Настраиваем новые поля для внешних треков
            entity.Property(t => t.ExternalAudioUrl).HasMaxLength(1024);
            entity.Property(t => t.ExternalCoverUrl).HasMaxLength(1024);
            entity.Property(t => t.IsExternal).HasDefaultValue(false);

            entity.Property(t => t.PlayCount).IsRequired().HasDefaultValue(0L);
        });

        base.OnModelCreating(modelBuilder);

        

        
    }
}