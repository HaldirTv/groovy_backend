using Groovra.Billing.Microservice.Models;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Billing.Microservice.Data;

public class BillingDbContext : DbContext
{
    public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options) { }

    public DbSet<UserSubscription> Subscriptions { get; set; }
    public DbSet<PaymentRecord> PaymentRecords { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserSubscription>()
            .HasIndex(s => s.UserId)
            .IsUnique();

        modelBuilder.Entity<PaymentRecord>()
            .HasIndex(p => p.UserId);

        modelBuilder.Entity<PaymentRecord>()
            .HasIndex(p => p.StripeSessionId);
    }
}
