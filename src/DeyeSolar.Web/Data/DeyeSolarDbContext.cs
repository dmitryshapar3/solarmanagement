using DeyeSolar.Domain.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public class DeyeSolarDbContext : IdentityDbContext<IdentityUser>
{
    public DeyeSolarDbContext(DbContextOptions<DeyeSolarDbContext> options) : base(options) { }

    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<ExportReading> ExportReadings => Set<ExportReading>();
    public DbSet<ExportPriceRow> ExportPrices => Set<ExportPriceRow>();
    public DbSet<TriggerRule> TriggerRules => Set<TriggerRule>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<RuleRunLog> RuleRunLogs => Set<RuleRunLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Reading>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => r.SolarObservedAt);
            e.Property(r => r.SolarDeviceSn).HasMaxLength(128);
        });

        modelBuilder.Entity<ExportReading>(e =>
        {
            e.HasKey(r => new { r.DeviceSn, r.ObservedAt });
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
            e.HasIndex(s => new { s.Section, s.Key }).IsUnique();
        });

        modelBuilder.Entity<RuleRunLog>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Timestamp);
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
}

public class ExportReading
{
    public string DeviceSn { get; set; } = string.Empty;
    public DateTime ObservedAt { get; set; }
    public int GridPowerWatts { get; set; }
    // Poll/fetch time fences a late older response from overwriting a newer correction.
    public DateTime PolledAt { get; set; }
}

public class Reading
{
    public int Id { get; set; }
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

public class RuleRunLog
{
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

public class AppSetting
{
    public int Id { get; set; }
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
