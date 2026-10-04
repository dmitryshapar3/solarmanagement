using System.Text.Json;

namespace SolarManagement.Integrations.Contracts;

public sealed record ProviderPackageIdentity(string ProviderId, string PackageVersion, string PackageDigest);
public sealed record IntegrationSelectOption(string Value, string Label);
public sealed record IntegrationFieldDescriptor(string Key, string Kind, string Label, bool Required,
    JsonElement? DefaultValue = null, decimal? Minimum = null, decimal? Maximum = null,
    IReadOnlyList<IntegrationSelectOption>? Options = null, bool Secret = false);
public sealed record IntegrationProviderDescriptor(string ProviderId, string PackageVersion, string PackageDigest,
    string DescriptorDigest, string DisplayName, int UiContractVersion, int ConfigurationVersion,
    IReadOnlyList<string> RequiredUiFeatures, IReadOnlyList<IntegrationFieldDescriptor> Fields,
    IReadOnlyList<string> Actions);
public interface IIntegrationProviderCatalog
{
    Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct);
    Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? packageVersion, CancellationToken ct);
    async Task<IReadOnlyList<IntegrationProviderDescriptor>> GetVersionsAsync(string providerId, CancellationToken ct)
        => (await GetProvidersAsync(ct)).Where(provider => provider.ProviderId == providerId).ToArray();
}
public sealed record IntegrationDraftConfiguration(JsonElement Values, IReadOnlyDictionary<string, string> Secrets);
public sealed record IntegrationTestResult(bool Success, string Code, string Message, string? AccountIdentity = null);
public sealed record IntegrationDiscoveryQuery(string? ParentId = null);
public sealed record IntegrationDiscoveredDevice(string RemoteId, string? Channel, string Kind, string Name,
    string? AccountIdentity, JsonElement? Metadata = null);
public interface IIntegrationSetupExecutor
{
    Task<IntegrationTestResult> TestAsync(ProviderPackageIdentity package, IntegrationDraftConfiguration draft, CancellationToken ct);
    Task<IReadOnlyList<IntegrationDiscoveredDevice>> DiscoverAsync(ProviderPackageIdentity package,
        IntegrationDraftConfiguration draft, IntegrationDiscoveryQuery query, CancellationToken ct);
}
public sealed record IntegrationSession(string InstallationId, Guid InstanceId, ProviderPackageIdentity Package,
    long ConfigurationRevision, long Generation, IntegrationDraftConfiguration Configuration);
public interface IIntegrationRuntimeExecutor
{
    Task<JsonElement> InvokeAsync(IntegrationSession session, string method, JsonElement parameters, CancellationToken ct);
    Task StopAsync(Guid instanceId, CancellationToken ct);
    // This releases local eviction bookkeeping after a durable uncertain close; it does not cancel a remote operation.
    Task ReleaseCommandTrackingAsync(Guid instanceId, Guid commandId, CancellationToken ct) => Task.CompletedTask;
}
public enum ProviderMeasurementQuality { Good, Missing, Invalid }
public sealed record ProviderMeasurement(decimal? Value, DateTimeOffset? ObservedAt, ProviderMeasurementQuality Quality);
public sealed record ProviderInverterTelemetry(string RemoteId, DateTimeOffset ReceivedAt, string SolarBasis,
    ProviderMeasurement BatterySoc, ProviderMeasurement BatteryPower, ProviderMeasurement BatteryTemperature,
    ProviderMeasurement BatteryVoltage, ProviderMeasurement BatteryCurrent, ProviderMeasurement SolarPower,
    ProviderMeasurement GridPower, ProviderMeasurement LoadPower);
public sealed record ProviderSocketTelemetry(string RemoteId, string? Channel, bool? IsOn, bool? Online,
    int? CurrentPowerWatts, DateTimeOffset? ObservedAt, DateTimeOffset ReceivedAt);
public sealed record ProviderSocketCommandResult(string CommandId, string Status, string? OperationToken = null,
    ProviderSocketTelemetry? ObservedState = null);
public sealed record ProviderGridSample(DateTimeOffset ObservedAt, int PowerWatts);
public sealed record ProviderGridHistory(string RemoteId, IReadOnlyList<ProviderGridSample> Samples,
    string? ContinuationToken, bool IsComplete);
public sealed record WorkerConfiguration(string ProviderId, Guid InstanceId, long ConfigurationRevision,
    long Generation, IntegrationDraftConfiguration Configuration, IReadOnlyList<string> AllowedOrigins,
    int MaximumOperationTimeoutSeconds = 300);
public sealed record WorkerHandshake(int WireVersion, string ProviderId, IReadOnlyList<string> Operations);
public sealed record IntegrationPackageManifest(string ProviderId, string PackageVersion, string PublisherId,
    int WireVersion, string RuntimeIdentifier, string EntryPoint, IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> AllowedOrigins, IntegrationProviderDescriptor Descriptor);
public sealed record IntegrationPackageInstallRequest(string ArchivePath, string ExpectedSha256);
public sealed record IntegrationInstalledPackage(ProviderPackageIdentity Identity, string ArtifactPath,
    IntegrationPackageManifest Manifest, DateTimeOffset InstalledAt);
public interface IIntegrationPackageManager
{
    Task<IntegrationInstalledPackage> InstallAsync(IntegrationPackageInstallRequest request, CancellationToken ct);
    Task<IntegrationInstalledPackage> ResolveAsync(ProviderPackageIdentity identity, CancellationToken ct);
}
public static class IntegrationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
