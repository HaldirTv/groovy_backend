using Groovra.History.Microservice.Data;
using Microsoft.EntityFrameworkCore;

namespace Groovra.History.Microservice.Data;  

public class HistoryDbContext : DbContext
{
    public HistoryDbContext(DbContextOptions<HistoryDbContext> options) : base(options)
    {
    }

    public DbSet<PlaybackHistory> PlaybackHistories => Set<PlaybackHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);


        modelBuilder.HasDefaultSchema("history");

        modelBuilder.Entity<PlaybackHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId); 
        });
    }
}