using Microsoft.EntityFrameworkCore;

namespace Groovra.ChatService.Microservice.Data;

public class ChatDbContext : DbContext
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
    {
    }

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> Participants => Set<ConversationParticipant>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageDeletion> MessageDeletions => Set<MessageDeletion>();
    public DbSet<BlockedUser> BlockedUsers => Set<BlockedUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("chat");

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasMany(e => e.Participants)
                .WithOne(p => p.Conversation)
                .HasForeignKey(p => p.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Messages)
                .WithOne(m => m.Conversation)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationParticipant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ConversationId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);

            entity.Property(e => e.ClearedAt)
                .HasConversion(
                    v => v,
                    v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ConversationId);

            // SQL Server datetime2 не зберігає DateTimeKind — при читанні EF Core завжди
            // повертає Kind=Unspecified, через що фронтенд парсить UTC-момент як локальний час.
            // Той самий фікс, що вже застосований в HistoryDbContext.
            entity.Property(e => e.CreatedAt)
                .HasConversion(
                    v => v,
                    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            entity.Property(e => e.EditedAt)
                .HasConversion(
                    v => v,
                    v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

            entity.HasMany<MessageDeletion>()
                .WithOne(d => d.Message)
                .HasForeignKey(d => d.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageDeletion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.MessageId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);

            entity.Property(e => e.DeletedAt)
                .HasConversion(
                    v => v,
                    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        });

        modelBuilder.Entity<BlockedUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.BlockerUserId, e.BlockedUserId }).IsUnique();
            entity.HasIndex(e => e.BlockedUserId);

            entity.Property(e => e.CreatedAt)
                .HasConversion(
                    v => v,
                    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        });
    }
}
