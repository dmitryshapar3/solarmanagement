using DeyeSolar.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public class DeyeSolarDbContext : DbContext
{
    public DeyeSolarDbContext(DbContextOptions<DeyeSolarDbContext> options) : base(options) { }

    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<TriggerRule> TriggerRules => Set<TriggerRule>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<RuleRunLog> RuleRunLogs => Set<RuleRunLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reading>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Timestamp);
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
