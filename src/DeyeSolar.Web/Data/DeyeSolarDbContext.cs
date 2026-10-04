using DeyeSolar.Domain.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public partial class DeyeSolarDbContext : IdentityDbContext<IdentityUser>
{
    public DeyeSolarDbContext(DbContextOptions<DeyeSolarDbContext> options) : base(options) { }
    public DeyeSolarDbContext(DbContextOptions<DeyeSolarDbContext> options, string installationId) : base(options)
    {
        if (string.IsNullOrWhiteSpace(installationId) || installationId.Length > 64)
            throw new ArgumentException("A valid installation is required.", nameof(installationId));
        InstallationId = installationId;
    }

    public string? InstallationId { get; }
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<InstallationMembership> InstallationMemberships => Set<InstallationMembership>();

    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<ExportReading> ExportReadings => Set<ExportReading>();
    public DbSet<ExportPriceRow> ExportPrices => Set<ExportPriceRow>();
    public DbSet<TriggerRule> TriggerRules => Set<TriggerRule>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<RuleRunLog> RuleRunLogs => Set<RuleRunLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureBilling(modelBuilder);
        ConfigureDynamicIntegrations(modelBuilder);
        modelBuilder.Entity<Installation>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasMaxLength(64);
            e.Property(i => i.Name).HasMaxLength(128);
        });
        modelBuilder.Entity<InstallationMembership>(e =>
        {
            e.HasKey(m => new { m.UserId, m.InstallationId });
            e.Property(m => m.InstallationId).HasMaxLength(64);
            e.Property(m => m.Role).HasMaxLength(32);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Installation).WithMany().HasForeignKey(m => m.InstallationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<IdentityUser>().HasIndex(u => u.PhoneNumber).IsUnique()
            .HasFilter("[PhoneNumber] IS NOT NULL AND [PhoneNumber] <> ''");
        modelBuilder.Entity<IdentityUser>().HasIndex(u => u.NormalizedEmail).IsUnique()
            .HasFilter("[NormalizedEmail] IS NOT NULL");
        ConfigureInstallation<Reading>(modelBuilder);
        ConfigureInstallation<ExportReading>(modelBuilder);
        ConfigureInstallation<TriggerRule>(modelBuilder);
        ConfigureInstallation<AppSetting>(modelBuilder);
        ConfigureInstallation<RuleRunLog>(modelBuilder);
        modelBuilder.Entity<Reading>().HasQueryFilter(r => InstallationId != null && r.InstallationId == InstallationId);
        modelBuilder.Entity<ExportReading>().HasQueryFilter(r => InstallationId != null && r.InstallationId == InstallationId);
        modelBuilder.Entity<TriggerRule>().HasQueryFilter(r => InstallationId != null && r.InstallationId == InstallationId);
        modelBuilder.Entity<AppSetting>().HasQueryFilter(r => InstallationId != null && r.InstallationId == InstallationId);
        modelBuilder.Entity<RuleRunLog>().HasQueryFilter(r => InstallationId != null && r.InstallationId == InstallationId);
        modelBuilder.Entity<Reading>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.InstallationId, r.Timestamp });
            e.HasIndex(r => new { r.InstallationId, r.SolarObservedAt });
            e.Property(r => r.SolarDeviceSn).HasMaxLength(128);
        });

        modelBuilder.Entity<ExportReading>(e =>
        {
            e.HasKey(r => new { r.InstallationId, r.DeviceSn, r.ObservedAt });
            e.Property(r => r.DeviceSn).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        });

        modelBuilder.Entity<ExportPriceRow>(e =>
        {
            e.HasKey(row => row.StartUtc);
            e.Property(row => row.PricePlnPerMwh).HasPrecision(18, 6);
        });

        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.InstallationId, s.Section, s.Key }).IsUnique();
        });

        modelBuilder.Entity<RuleRunLog>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.InstallationId, r.Timestamp });
            e.Property(r => r.ConditionKey).HasMaxLength(160);
        });

        modelBuilder.Entity<TriggerRule>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.ActiveFrom).HasConversion(
                v => v.HasValue ? v.Value.ToString("HH:mm") : null,
                v => v != null ? TimeOnly.Parse(v) : null);
            e.Property(r => r.ActiveTo).HasConversion(
                v => v.HasValue ? v.Value.ToString("HH:mm") : null,
                v => v != null ? TimeOnly.Parse(v) : null);
        });

    }

    private static void ConfigureInstallation<TEntity>(ModelBuilder modelBuilder) where TEntity : class, IInstallationOwned
    {
        modelBuilder.Entity<TEntity>().Property(e => e.InstallationId).HasMaxLength(64).IsConcurrencyToken();
        modelBuilder.Entity<TEntity>().HasOne<Installation>().WithMany().HasForeignKey(e => e.InstallationId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceInstallationWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceInstallationWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceInstallationWrites()
    {
        AddNewBillingAccounts();
        foreach (var entry in ChangeTracker.Entries<IInstallationOwned>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (InstallationId is null) throw new InvalidOperationException("Private writes require an installation.");
            if (entry.State == EntityState.Added && string.IsNullOrEmpty(entry.Entity.InstallationId))
                entry.Entity.InstallationId = InstallationId;
            if (entry.Entity.InstallationId != InstallationId
                || entry.State != EntityState.Added && entry.Property(nameof(IInstallationOwned.InstallationId)).OriginalValue as string != InstallationId)
                throw new InvalidOperationException("A private row cannot be accessed across installations.");
        }
    }
}

public class ExportReading : IInstallationOwned
{
    public string InstallationId { get; set; } = string.Empty;
    public string DeviceSn { get; set; } = string.Empty;
    public DateTime ObservedAt { get; set; }
    public int GridPowerWatts { get; set; }
    // Poll/fetch time fences a late older response from overwriting a newer correction.
    public DateTime PolledAt { get; set; }
}

public class Reading : IInstallationOwned
{
    public string InstallationId { get; set; } = string.Empty;
    public int Id { get; set; }
    public Guid? InverterId { get; set; }
    public bool? BatterySocValid { get; set; }
    public long ConfigurationRevision { get; set; }
    public long RuntimeGeneration { get; set; }
    public DateTime Timestamp { get; set; }
    public int BatterySoc { get; set; }
    public double BatteryTemperature { get; set; }
    public double BatteryVoltage { get; set; }
    public int BatteryPower { get; set; }
    public double BatteryCurrent { get; set; }
    public int SolarProduction { get; set; }
    public DateTime? SolarObservedAt { get; set; }
    public string? SolarDeviceSn { get; set; }
    public int GridConsumption { get; set; }
    public int LoadPower { get; set; }
    public string DataSource { get; set; } = string.Empty;
}

public class RuleRunLog : IInstallationOwned
{
    public string InstallationId { get; set; } = string.Empty;
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ConditionKey { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int BatterySoc { get; set; }
    public int SolarProduction { get; set; }
    public int BatteryPower { get; set; }
}

public class AppSetting : IInstallationOwned
{
    public string InstallationId { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
