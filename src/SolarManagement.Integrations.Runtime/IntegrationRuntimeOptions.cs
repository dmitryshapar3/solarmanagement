namespace SolarManagement.Integrations.Runtime;

public sealed class IntegrationRuntimeOptions
{
    public const string Section = "IntegrationRuntime";
    public string PackageDirectory { get; set; } = "integration-packages";
    public string DotnetExecutable { get; set; } = "dotnet";
    public int MaximumWorkers { get; set; } = 8;
    public int MaximumFrameBytes { get; set; } = 1024 * 1024;
    public int MaximumQueuedCalls { get; set; } = 64;
    public int MaximumRememberedInstances { get; set; } = 10000;
    public int RequestTimeoutSeconds { get; set; } = 45;
    public int MaximumNegotiatedRequestTimeoutSeconds { get; set; } = 300;
    public int IdleTimeoutSeconds { get; set; } = 300;
    public Dictionary<string, string> TrustedPublisherPublicKeys { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> TrustedPublisherPublicKeyFiles { get; set; } = new(StringComparer.Ordinal);
    public List<string> ApprovedOrigins { get; set; } = [];
    public List<IntegrationPackageBootstrapEntry> BootstrapPackages { get; set; } = [];
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PackageDirectory) || string.IsNullOrWhiteSpace(DotnetExecutable)
            || MaximumWorkers is < 1 or > 64 || MaximumFrameBytes is < 1024 or > 1024 * 1024
            || MaximumQueuedCalls is < 1 or > 1024 || RequestTimeoutSeconds is < 1 or > 300
            || MaximumNegotiatedRequestTimeoutSeconds < RequestTimeoutSeconds || MaximumNegotiatedRequestTimeoutSeconds > 300
            || MaximumRememberedInstances is < 1 or > 100000
            || IdleTimeoutSeconds is < 1 or > 86400)
            throw new ArgumentException("Invalid integration runtime limits.");
    }
}

public sealed class IntegrationPackageBootstrapEntry
{
    public string ArchivePath { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
}
