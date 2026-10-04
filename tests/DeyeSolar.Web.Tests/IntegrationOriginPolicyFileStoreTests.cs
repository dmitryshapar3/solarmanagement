using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Tests;

public class IntegrationOriginPolicyFileStoreTests
{
    [Fact]
    public async Task ApprovalsAreAuthenticatedAndPersistAcrossStoreReconstruction()
    {
        using var fixture = new Fixture();
        Assert.Empty(await fixture.Store.LoadAsync(default));
        await fixture.Store.SaveAsync(["https://provider.example"], default);
        Assert.DoesNotContain("https://provider.example", await File.ReadAllTextAsync(fixture.FilePath));
        var recreated = new IntegrationOriginPolicyFileStore(fixture.Secrets, fixture.Options);
        Assert.Equal("https://provider.example", Assert.Single(await recreated.LoadAsync(default)));
        await File.WriteAllTextAsync(fixture.FilePath, "[\"https://foreign.example\"]");
        await Assert.ThrowsAsync<InvalidDataException>(() => recreated.LoadAsync(default));
    }
    [Fact]
    public async Task FailedOrCancelledSavePreservesThePreviousProtectedPolicy()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(["https://provider.example"], default);
        var before = await File.ReadAllBytesAsync(fixture.FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.SaveAsync(Enumerable.Repeat("https://other.example", 129).ToArray(), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.SaveAsync(["https://other.example"], cancelled.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.FilePath));
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath));
    }
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "SolarOriginPolicy_" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(DirectoryPath, "operator-origins.protected");
        public IntegrationSecretStore Secrets { get; } = new(new EphemeralDataProtectionProvider());
        public Microsoft.Extensions.Options.IOptions<IntegrationRuntimeOptions> Options => Microsoft.Extensions.Options.Options.Create(new IntegrationRuntimeOptions { PackageDirectory = DirectoryPath });
        public IntegrationOriginPolicyFileStore Store => new(Secrets, Options);
        public void Dispose()
        {
            var absolute = Path.GetFullPath(DirectoryPath);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("SolarOriginPolicy_", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test directory.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
}
