using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Integrations.Runtime;

public sealed class IntegrationPackageBootstrap(IIntegrationPackageManager packages, IOptions<IntegrationRuntimeOptions> options)
{
    public async Task EnsureInstalledAsync(CancellationToken ct)
    {
        foreach (var entry in options.Value.BootstrapPackages)
            await packages.InstallAsync(new(entry.ArchivePath, entry.ExpectedSha256), ct);
    }
}
