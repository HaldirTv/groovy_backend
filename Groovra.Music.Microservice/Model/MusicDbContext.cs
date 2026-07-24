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
            entity.HasQueryFilter(t => !t.IsDeleted);

            // Без цього індексу кожен запит "ORDER BY PlayCount DESC" (популярне/рекомендації/
            // повний список) робить full scan + явний SORT на всій таблиці. На невеликій БД
            // (кілька треків) це непомітно, але після масового імпорту (сотні треків) SQL Server
            // просить memory grant під сортування і під конкурентним навантаженням чекає в черзі
            // (RESOURCE_SEMAPHORE) — саме це спричиняло зависання /music/tracks/recommendations
            // та сусідніх ендпоінтів.
            //
            // ПРИМІТКА: ідеально було б зробити цей індекс покривним (INCLUDE усіх колонок,
            // які реально вибираються) — тоді для SELECT * теж не знадобився б жоден memory
            // grant. Пробували; на цій машині (SQL Server зараз бачить лише ~150-300MB вільної
            // фізичної пам'яті — сторонні процеси розробника з'їдають решту) сама операція
            // ALTER/CREATE INDEX WITH INCLUDE стабільно не встигає за 30с і застрягала в
            // нескінченному retry-циклі при старті додатку. Тому зараз — простий (не покривний)
            // індекс, який ГАРАНТОВАНО застосовується і все одно суттєво прискорює прості
            // "ORDER BY PlayCount" запити без додаткових WHERE-фільтрів. Якщо в майбутньому
            // з'явиться вільна пам'ять на сервері БД, варто повернутись до покривної версії.
            entity.HasIndex(t => new { t.IsDeleted, t.PlayCount })
                .IsDescending(false, true)
                .HasDatabaseName("IX_Tracks_IsDeleted_PlayCount");
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
        
        
        modelBuilder.Entity<Album>(b =>
        {
            b.HasIndex(a => a.UserId);
            b.HasQueryFilter(a => !a.IsDeleted); // как у Playlist — скрываем soft-deleted по умолчанию
        });

        modelBuilder.Entity<Track>()
            .HasOne(t => t.Album)
            .WithMany(a => a.Tracks)
            .HasForeignKey(t => t.AlbumId)
            .OnDelete(DeleteBehavior.SetNull); // удаление альбома не должно валить треки

        modelBuilder.Entity<FavoriteAlbum>(b =>
        {
            b.HasKey(fa => new { fa.UserId, fa.AlbumId });
            b.HasOne(fa => fa.Album)
                .WithMany()
                .HasForeignKey(fa => fa.AlbumId)
                .OnDelete(DeleteBehavior.Cascade); // лайк удаляется вместе с альбомом
        });
        
        
        modelBuilder.Entity<FavoritePlaylist>(b =>
        {
            b.ToTable("FavoritePlaylists", "music");
            b.HasKey(fp => new { fp.UserId, fp.PlaylistId });

            b.HasOne(fp => fp.Playlist)
                .WithMany()
                .HasForeignKey(fp => fp.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade); // Лайк исчезает, если плейлист удален физически
        });

        modelBuilder.Entity<Download>(b =>
        {
            // The Type column is nvarchar(450) (see AddDownloads migration); without this
            // conversion EF defaults to storing/reading the enum as its numeric ordinal,
            // which doesn't match the string column and throws on read.
            b.Property(d => d.Type).HasConversion<string>();
        });

        modelBuilder.Entity<TrackComment>(b =>
        {
            b.ToTable("TrackComments", "music");
            b.HasKey(c => c.Id);
            b.Property(c => c.AuthorName).HasMaxLength(256);
            b.Property(c => c.Text).IsRequired().HasMaxLength(2000);
            b.HasIndex(c => c.TrackId);
            b.HasOne(c => c.Track)
                .WithMany()
                .HasForeignKey(c => c.TrackId)
                .OnDelete(DeleteBehavior.Cascade); // комментарии удаляются вместе с треком
            b.HasQueryFilter(c => !c.IsDeleted);
        });

        modelBuilder.Entity<TrackCommentLike>(b =>
        {
            b.ToTable("TrackCommentLikes", "music");
            b.HasKey(cl => cl.Id);
            b.HasIndex(cl => new { cl.CommentId, cl.UserId }).IsUnique();
            b.HasOne(cl => cl.Comment)
                .WithMany()
                .HasForeignKey(cl => cl.CommentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}