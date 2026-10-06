using DeyeSolar.Web.Redesign;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public partial class DeyeSolarDbContext
{
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    private void ConfigureRedesign(ModelBuilder modelBuilder)
    {
        ConfigureInstallation<ActivityEvent>(modelBuilder);
        modelBuilder.Entity<ActivityEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasQueryFilter(x => InstallationId != null && x.InstallationId == InstallationId);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.DeviceId).HasMaxLength(256);
            e.Property(x => x.RuleName).HasMaxLength(256);
            e.Property(x => x.ConfigurationVersion).HasMaxLength(128);
            e.Property(x => x.ReasonCode).HasMaxLength(100);
            e.Property(x => x.ActorUserId).HasMaxLength(450);
            e.Property(x => x.Client).HasMaxLength(80);
            e.HasIndex(x => new { x.InstallationId, x.OccurredAt, x.Id });
            e.HasIndex(x => new { x.InstallationId, x.RuleId, x.OccurredAt });
            e.HasIndex(x => new { x.InstallationId, x.DeviceId, x.OccurredAt });
            e.HasIndex(x => new { x.InstallationId, x.GroupId, x.Id });
        });
        modelBuilder.Entity<DeyeSolar.Domain.Models.TriggerRule>(e =>
        {
            e.Property(x => x.PauseReason).HasMaxLength(40);
            e.Property(x => x.PausedByUserId).HasMaxLength(450);
        });
        modelBuilder.Entity<Integrations.IntegrationCommandEntity>(e =>
        {
            e.Property(x => x.ActorUserId).HasMaxLength(450);
            e.Property(x => x.Client).HasMaxLength(80);
            e.Property(x => x.OnRuleConflict).HasMaxLength(10);
        });
    }
}
