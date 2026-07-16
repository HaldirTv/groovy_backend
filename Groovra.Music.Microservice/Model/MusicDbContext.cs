using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Model;

public class MusicDbContext : DbContext
{
    public MusicDbContext(DbContextOptions<MusicDbContext> options) : base(options) { }

    public DbSet<Track> Tracks { get; set; }
    public DbSet<FavoriteTrack> FavoriteTracks { get; set; }
    public DbSet<Playlist> Playlists { get; set; }
    public DbSet<PlaylistTrack> PlaylistTracks { get; set; }
    public DbSet<Download> Downloads { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
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
            
            entity.Property(t => t.ExternalAudioUrl).HasMaxLength(1024);
            entity.Property(t => t.ExternalCoverUrl).HasMaxLength(1024);
            entity.Property(t => t.IsExternal).HasDefaultValue(false);
            
            entity.Property(t => t.PlayCount).IsRequired().HasDefaultValue(0L);
        });
        
        modelBuilder.Entity<Playlist>(b =>
        {   
            b.ToTable("Playlists", "music");
            b.HasKey(p => p.Id);

            // Индексы и ограничения для новых фич
            b.Property(p => p.Slug).HasMaxLength(300);
            b.HasIndex(p => p.Slug).IsUnique(); // Слаг обязан быть уникальным
            b.Property(p => p.CoverImageUrl).HasMaxLength(1024);

            b.Property(p => p.TrackCount).HasDefaultValue(0);
            b.Property(p => p.TotalDurationSeconds).HasDefaultValue(0);

            // Глобальный фильтр мягкого удаления
            b.HasQueryFilter(p => !p.IsDeleted);
        });

        modelBuilder.Entity<PlaylistTrack>(b =>
        {   
            b.ToTable("PlaylistTracks", "music");
            b.HasKey(pt => new { pt.PlaylistId, pt.TrackId });

            b.HasOne(pt => pt.Playlist)
                .WithMany(p => p.Tracks)
                .HasForeignKey(pt => pt.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(pt => pt.Track)
                .WithMany()
                .HasForeignKey(pt => pt.TrackId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FavoriteTrack>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.HasIndex(f => new { f.UserId, f.TrackId }).IsUnique();
            entity.HasOne(f => f.Track)
                  .WithMany()
                  .HasForeignKey(f => f.TrackId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Download>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Type).HasConversion<string>();
            entity.HasIndex(d => new { d.UserId, d.Type, d.ItemId }).IsUnique();
        });
    }
}
