using System.Text.Json;

namespace DeyeSolar.Web.Integrations;

public sealed record IntegrationInstanceDto(Guid Id, string ProviderId, string Name, string Status,
    long Revision, long Generation, string PackageVersion, string PackageDigest, string DescriptorDigest);
public sealed record IntegrationConfigurationDto(IntegrationInstanceDto Instance,
    IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyDictionary<string, bool> SecretPresent);
public sealed record CreateIntegrationRequest(string ProviderId, string Name);
public sealed record IntegrationVersionGuard(long ExpectedRevision, string PackageVersion,
    string PackageDigest, string DescriptorDigest);
public sealed record IntegrationPackageChange(IntegrationVersionGuard Guard, string TargetPackageVersion);
public sealed record IntegrationSecretOperation(string Operation, string? Value = null);
public sealed record IntegrationConfigurationChange(long ExpectedRevision, string PackageVersion,
    string PackageDigest, string DescriptorDigest, Dictionary<string, JsonElement> Values,
    Dictionary<string, IntegrationSecretOperation> SecretOperations);
public sealed record IntegrationBindingDto(Guid Id, Guid InstanceId, string Kind, string Name,
    string RemoteId, string Channel, bool IsDefault);
public sealed record IntegrationDiscoveryDevice(string SelectionToken, string Name, string Kind,
    string RemoteId, string Channel, JsonElement? Metadata);
public sealed record IntegrationDiscoveryResponse(IReadOnlyList<IntegrationDiscoveryDevice> Devices,
    DateTimeOffset ExpiresAt);
public sealed record SelectIntegrationDeviceRequest(IntegrationConfigurationChange Draft, string SelectionToken);
public sealed record IntegrationApiError(string Code, string Message);

public sealed class IntegrationRequestException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public static class IntegrationMappings
{
    public static IntegrationInstanceDto ToDto(this IntegrationInstanceEntity value) => new(value.Id,
        value.ProviderId, value.Name, value.State, value.Revision, value.Generation,
        value.PackageVersion, value.PackageDigest, value.DescriptorDigest);
    public static IntegrationBindingDto ToDto(this IntegrationDeviceBindingEntity value) => new(value.Id,
        value.InstanceId, value.Kind, value.Name, value.RemoteId, value.Channel, value.IsDefault);
}
