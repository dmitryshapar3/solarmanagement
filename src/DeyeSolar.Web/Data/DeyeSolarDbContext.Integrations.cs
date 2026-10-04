using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public partial class DeyeSolarDbContext
{
    public DbSet<IntegrationInstanceEntity> IntegrationInstances => Set<IntegrationInstanceEntity>();
    public DbSet<IntegrationConfigurationEntity> IntegrationConfigurations => Set<IntegrationConfigurationEntity>();
    public DbSet<IntegrationDeviceBindingEntity> IntegrationDeviceBindings => Set<IntegrationDeviceBindingEntity>();
    public DbSet<IntegrationCommandEntity> IntegrationCommands => Set<IntegrationCommandEntity>();
    public DbSet<IntegrationOAuthFlowEntity> IntegrationOAuthFlows => Set<IntegrationOAuthFlowEntity>();

    private void ConfigureDynamicIntegrations(ModelBuilder modelBuilder)
    {
        ConfigureInstallation<IntegrationInstanceEntity>(modelBuilder);
        ConfigureInstallation<IntegrationConfigurationEntity>(modelBuilder);
        ConfigureInstallation<IntegrationDeviceBindingEntity>(modelBuilder);
        ConfigureInstallation<IntegrationCommandEntity>(modelBuilder);
        ConfigureInstallation<IntegrationOAuthFlowEntity>(modelBuilder);
        modelBuilder.Entity<IntegrationOAuthFlowEntity>(e =>
        {
            e.ToTable("IntegrationOAuthFlows");
            e.HasKey(x => x.Id);
            e.HasQueryFilter(x => InstallationId != null && x.InstallationId == InstallationId);
            e.Property(x => x.UserId).HasMaxLength(450);
            e.Property(x => x.SecurityStamp).HasMaxLength(256);
            e.Property(x => x.StateHash).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.Client).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(32).IsConcurrencyToken();
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.PackageVersion).HasMaxLength(64);
            e.Property(x => x.PackageDigest).HasMaxLength(128);
            e.Property(x => x.DescriptorDigest).HasMaxLength(128);
            e.HasIndex(x => x.StateHash).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => new { x.InstallationId, x.InstanceId, x.UserId, x.ExpiresAt });
            e.HasOne<IntegrationInstanceEntity>().WithMany().HasForeignKey(x => new { x.InstallationId, x.InstanceId })
                .HasPrincipalKey(x => new { x.InstallationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Microsoft.AspNetCore.Identity.IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<IntegrationInstanceEntity>(e =>
        {
            e.ToTable("IntegrationInstances");
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.InstallationId, x.Id });
            e.HasQueryFilter(x => InstallationId != null && x.InstallationId == InstallationId);
            e.Property(x => x.ProviderId).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.PackageVersion).HasMaxLength(64);
            e.Property(x => x.PackageDigest).HasMaxLength(128);
            e.Property(x => x.DescriptorDigest).HasMaxLength(128);
            e.Property(x => x.State).HasMaxLength(32);
            e.Property(x => x.AccountIdentity).HasMaxLength(256);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.Generation).IsConcurrencyToken();
        });
        modelBuilder.Entity<IntegrationConfigurationEntity>(e =>
        {
            e.ToTable("IntegrationConfigurations");
            e.HasKey(x => new { x.InstallationId, x.InstanceId, x.Revision });
            e.HasQueryFilter(x => InstallationId != null && x.InstallationId == InstallationId);
            e.HasOne<IntegrationInstanceEntity>().WithMany()
                .HasForeignKey(x => new { x.InstallationId, x.InstanceId })
                .HasPrincipalKey(x => new { x.InstallationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<IntegrationDeviceBindingEntity>(e =>
        {
            e.ToTable("IntegrationDeviceBindings");
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.InstallationId, x.InstanceId, x.Id });
            e.HasAlternateKey(x => new { x.InstallationId, x.Id });
            e.HasQueryFilter(x => InstallationId != null && x.InstallationId == InstallationId);
            e.Property(x => x.RemoteId).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.Channel).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.AddedByUserId).HasMaxLength(450);
            e.HasIndex(x => new { x.AddedByUserId, x.Kind });
            e.HasOne<Microsoft.AspNetCore.Identity.IdentityUser>().WithMany().HasForeignKey(x => x.AddedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.AccountIdentity).HasMaxLength(256);
            e.HasIndex(x => new { x.InstallationId, x.InstanceId, x.Kind, x.RemoteId, x.Channel }).IsUnique();
            e.HasIndex(x => new { x.InstallationId, x.Kind }).IsUnique()
                .HasFilter("[IsDefault] = 1 AND [Enabled] = 1");
            e.HasOne<IntegrationInstanceEntity>().WithMany()
                .HasForeignKey(x => new { x.InstallationId, x.InstanceId })
                .HasPrincipalKey(x => new { x.InstallationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<IntegrationCommandEntity>(e =>
        {
            e.ToTable("IntegrationCommands");
            e.HasKey(x => new { x.InstallationId, x.Id });
            e.HasQueryFilter(x => InstallationId != null && x.InstallationId == InstallationId);
            e.Property(x => x.Status).HasMaxLength(32).IsConcurrencyToken();
            e.Property(x => x.ProviderOperationId).HasMaxLength(256);
            e.Property(x => x.PayloadHash).HasMaxLength(128);
            e.Property(x => x.ErrorCode).HasMaxLength(128);
            e.HasIndex(x => new { x.InstallationId, x.DeviceId, x.CreatedAt });
            e.HasOne<IntegrationDeviceBindingEntity>().WithMany()
                .HasForeignKey(x => new { x.InstallationId, x.InstanceId, x.DeviceId })
                .HasPrincipalKey(x => new { x.InstallationId, x.InstanceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
