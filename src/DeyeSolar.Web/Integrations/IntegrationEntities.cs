using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Integrations;

public sealed class IntegrationInstanceEntity : IInstallationOwned
{
    public Guid Id { get; set; }
    public string InstallationId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Name { get; set; } = "";
    public string PackageVersion { get; set; } = "";
    public string PackageDigest { get; set; } = "";
    public string DescriptorDigest { get; set; } = "";
    public int ConfigurationVersion { get; set; }
    public long Revision { get; set; } = 1;
    public long Generation { get; set; } = 1;
    public string State { get; set; } = "draft";
    public string? AccountIdentity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class IntegrationConfigurationEntity : IInstallationOwned
{
    public string InstallationId { get; set; } = "";
    public Guid InstanceId { get; set; }
    public long Revision { get; set; }
    public string ValuesJson { get; set; } = "{}";
    public string SecretsCiphertext { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class IntegrationDeviceBindingEntity : IInstallationOwned
{
    public Guid Id { get; set; }
    public string InstallationId { get; set; } = "";
    public Guid InstanceId { get; set; }
    public string RemoteId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? AddedByUserId { get; set; }
    public string Name { get; set; } = "";
    public string? AccountIdentity { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class IntegrationCommandEntity : IInstallationOwned
{
    public Guid Id { get; set; }
    public string InstallationId { get; set; } = "";
    public Guid DeviceId { get; set; }
    public Guid InstanceId { get; set; }
    public long Revision { get; set; }
    public long Generation { get; set; }
    public bool DesiredState { get; set; }
    public string Status { get; set; } = "requested";
    public string? ProviderOperationId { get; set; }
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
}
