using DeyeSolar.Web.Data;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Integrations;

public interface IIntegrationConfigurationWriter
{
    void AppendRevision(DeyeSolarDbContext db, IntegrationInstanceEntity instance, IntegrationDraftConfiguration configuration);
}

/// <summary>Appends encrypted immutable configuration revisions inside a guarded settings transaction.</summary>
public sealed class IntegrationConfigurationWriter(IntegrationSecretStore secrets, TimeProvider clock,
    IIntegrationConnectionLifecycle lifecycle) : IIntegrationConfigurationWriter
{
    public void AppendRevision(DeyeSolarDbContext db, IntegrationInstanceEntity instance, IntegrationDraftConfiguration configuration)
    {
        instance.Revision++;
        lifecycle.Touch(instance);
        db.Add(new IntegrationConfigurationEntity
        {
            InstallationId = instance.InstallationId, InstanceId = instance.Id, Revision = instance.Revision,
            ValuesJson = configuration.Values.GetRawText(),
            SecretsCiphertext = secrets.Encrypt(instance.InstallationId, instance.Id, instance.Revision, configuration.Secrets),
            CreatedAt = clock.GetUtcNow()
        });
    }
}
