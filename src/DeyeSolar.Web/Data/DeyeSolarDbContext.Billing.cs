using DeyeSolar.Web.Billing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public partial class DeyeSolarDbContext
{
    public DbSet<BillingAccount> BillingAccounts => Set<BillingAccount>();
    public DbSet<AppleSubscription> AppleSubscriptions => Set<AppleSubscription>();

    private static void ConfigureBilling(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BillingAccount>(e =>
        {
            e.HasKey(a => a.UserId);
            e.Ignore(a => a.TrialEndsAt);
            e.HasIndex(a => a.AppAccountToken).IsUnique();
            e.HasOne<IdentityUser>().WithOne().HasForeignKey<BillingAccount>(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AppleSubscription>(e =>
        {
            e.HasKey(s => s.OriginalTransactionId);
            e.Property(s => s.OriginalTransactionId).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
            e.Property(s => s.TransactionId).HasMaxLength(64);
            e.Property(s => s.ProductId).HasMaxLength(200);
            e.Property(s => s.Environment).HasMaxLength(16);
            e.Property(s => s.ObservationStartedAt).IsConcurrencyToken();
            e.HasOne<IdentityUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private void AddNewBillingAccounts()
    {
        foreach (var entry in ChangeTracker.Entries<IdentityUser>().Where(e => e.State == EntityState.Added).ToList())
            if (!BillingAccounts.Local.Any(a => a.UserId == entry.Entity.Id))
                BillingAccounts.Add(BillingAccount.Create(entry.Entity.Id, DateTimeOffset.UtcNow));
    }
}
