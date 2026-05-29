using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Model;

public class MusicDbContext : DbContext
{
    public MusicDbContext(DbContextOptions<MusicDbContext> options) : base(options) { }

    public DbSet<Track> Tracks { get; set; }
}