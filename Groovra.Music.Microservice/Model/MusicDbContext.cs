using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Model;

public class MusicDbContext : DbContext
{
    public MusicDbContext(DbContextOptions<MusicDbContext> options) : base(options) { }

    public DbSet<Track> Tracks { get; set; }
    public DbSet<FavoriteTrack> FavoriteTracks { get; set; }
    public DbSet<Playlist> Playlists { get; set; }
    public DbSet<PlaylistTrack> PlaylistTracks { get; set; }
    public DbSet<Album> Albums { get; set; }
    public DbSet<FavoriteAlbum> FavoriteAlbums { get; set; }
    public DbSet<FavoritePlaylist> FavoritePlaylists { get; set; }
    public DbSet<Download> Downloads { get; set; }
    public DbSet<TrackComment> TrackComments { get; set; }
    public DbSet<TrackCommentLike> TrackCommentLikes { get; set; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("music");

        modelBuilder.Entity<Track>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Title).IsRequired().HasMaxLength(256);
            entity.Property(t => t.ArtistName).IsRequired().HasMaxLength(256);
            entity.Property(t => t.AlbumTitle).HasMaxLength(256);
            entity.Property(t => t.Genre).HasMaxLength(128);
            entity.Property(t => t.Mood).HasMaxLength(64);
            entity.Property(t => t.ContentType).HasMaxLength(128);
            entity.Property(t => t.AudioRelativePath).HasMaxLength(512); 
            entity.Property(t => t.CoverImageRelativePath).HasMaxLength(512);
            entity.Property(t => t.ExternalAudioUrl).HasMaxLength(1024);
            entity.Property(t => t.ExternalCoverUrl).HasMaxLength(1024);
            entity.Property(t => t.IsExternal).HasDefaultValue(false);
            entity.Property(t => t.PlayCount).IsRequired().HasDefaultValue(0L);
            entity.HasIndex(t => new { t.IsDeleted, t.PlayCount }).IsDescending(false, true).HasDatabaseName("IX_Tracks_IsDeleted_PlayCount");
            entity.HasQueryFilter(t => !t.IsDeleted);
        });
        modelBuilder.Entity<Playlist>(b =>
        {   
            b.ToTable("Playlists", "music");
            b.HasKey(p => p.Id);

            b.Property(p => p.Slug).HasMaxLength(300);
            b.HasIndex(p => p.Slug).IsUnique(); 
            b.Property(p => p.CoverImageUrl).HasMaxLength(1024);

            b.Property(p => p.TrackCount).HasDefaultValue(0);
            b.Property(p => p.TotalDurationSeconds).HasDefaultValue(0);

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
        modelBuilder.Entity<Album>(b =>
        {
            b.HasIndex(a => a.UserId);
            b.HasQueryFilter(a => !a.IsDeleted); 
        });

        modelBuilder.Entity<Track>()
            .HasOne(t => t.Album)
            .WithMany(a => a.Tracks)
            .HasForeignKey(t => t.AlbumId)
            .OnDelete(DeleteBehavior.SetNull); 

        modelBuilder.Entity<FavoriteAlbum>(b =>
        {
            b.HasKey(fa => new { fa.UserId, fa.AlbumId });
            b.HasOne(fa => fa.Album)
                .WithMany()
                .HasForeignKey(fa => fa.AlbumId)
                .OnDelete(DeleteBehavior.Cascade); 
        });
        modelBuilder.Entity<FavoritePlaylist>(b =>
        {
            b.ToTable("FavoritePlaylists", "music");
            b.HasKey(fp => new { fp.UserId, fp.PlaylistId });
            b.HasOne(fp => fp.Playlist)
                .WithMany()
                .HasForeignKey(fp => fp.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade); 
        });

        modelBuilder.Entity<Download>(b =>
        {
            b.HasKey(d => d.Id);
            // Live DB has Type as nvarchar(450) (from an earlier migration), not int -
            // must match the physical column or reads/writes throw a conversion error.
            b.Property(d => d.Type).HasConversion<string>();
            b.Property(d => d.AlbumName).HasMaxLength(256);
            b.Property(d => d.ArtistName).HasMaxLength(256);
            b.HasIndex(d => new { d.UserId, d.Type, d.ItemId }).IsUnique();
            b.HasIndex(d => new { d.UserId, d.Type, d.AlbumName, d.ArtistName }).IsUnique();
        });
        modelBuilder.Entity<TrackComment>(b =>
        {
            b.ToTable("TrackComments", "music");
            b.HasKey(c => c.Id);
            b.HasIndex(c => c.TrackId);
            b.Property(c => c.AuthorName).HasMaxLength(256);
            b.Property(c => c.Text).IsRequired().HasMaxLength(2000);
            b.HasQueryFilter(c => !c.IsDeleted);
        });

        modelBuilder.Entity<TrackCommentLike>(b =>
        {
            b.ToTable("TrackCommentLikes", "music");
            b.HasKey(cl => cl.Id);
            b.HasIndex(cl => new { cl.CommentId, cl.UserId }).IsUnique();
        });
    }
}