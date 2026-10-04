using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;
using SolarManagement.Integrations.WorkerSdk;

namespace SolarManagement.Integrations.Tests;

public sealed class PackageAndRuntimeTests
{
    [Fact]
    public async Task PublishedPackagingToolBuildsTrustedBootstrapArtifactWithoutPrivateKeyInPayload()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tool = Path.Combine(AppContext.BaseDirectory, "packager", "SolarManagement.IntegrationPackager.dll");
        var privateKey = Path.Combine(fixture.DirectoryPath, "publisher-private.pem");
        var publicKey = Path.Combine(fixture.DirectoryPath, "publisher-public.pem");
        await RunToolAsync(tool, "--generate-key", privateKey, publicKey);
        var originalKeyDigest = SHA256.HashData(await File.ReadAllBytesAsync(privateKey));
        await RunToolAsync(tool, false, "--generate-key", privateKey, publicKey);
        Assert.Equal(originalKeyDigest, SHA256.HashData(await File.ReadAllBytesAsync(privateKey)));
        await RunToolAsync(tool, "--check-key-pair", privateKey, publicKey);
        await RunToolAsync(tool, false, "--check-key-pair", privateKey, privateKey);
        var descriptor = new IntegrationProviderDescriptor("test.provider", "4.0.0", "", "", "Packaging test", 1, 1, ["text"],
            [new("account", "text", "Account", true)], ["test", "discover"]);
        var manifest = new IntegrationPackageManifest("test.provider", "4.0.0", "test-publisher", 1, "portable",
            "SolarManagement.IntegrationTestWorker.dll", new Dictionary<string, string>(), [], descriptor);
        var template = Path.Combine(fixture.DirectoryPath, "manifest-template.json");
        await File.WriteAllTextAsync(template, JsonSerializer.Serialize(manifest, IntegrationJson.Options));
        var output = Path.Combine(fixture.DirectoryPath, "packaged.zip");
        var digest = await RunToolAsync(tool, Path.Combine(AppContext.BaseDirectory, "worker"), template, privateKey, output);
        fixture.Options.TrustedPublisherPublicKeys.Clear();
        fixture.Options.TrustedPublisherPublicKeyFiles["test-publisher"] = publicKey;
        var store = new IntegrationPackageStore(Options.Create(fixture.Options));
        var bootstrap = new IntegrationPackageBootstrap(store, Options.Create(fixture.Options));
        fixture.Options.BootstrapPackages.Add(new() { ArchivePath = output, ExpectedSha256 = digest.Trim() });
        await bootstrap.EnsureInstalledAsync(default);
        await bootstrap.EnsureInstalledAsync(default);
        var restarted = new IntegrationPackageStore(Options.Create(fixture.Options));
        var package = Assert.Single(await restarted.GetProvidersAsync(default));
        Assert.Equal("4.0.0", package.PackageVersion);
        using var archive = ZipFile.OpenRead(output);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".pem", StringComparison.Ordinal));
        Assert.Empty(fixture.Options.TrustedPublisherPublicKeys);
    }
    [Fact]
    public async Task InstallAuthenticatesEveryPayloadAndSurvivesRestartWithoutDuplicateVersion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var package = await fixture.InstallAsync();
        var restarted = new IntegrationPackageStore(Options.Create(fixture.Options));
        Assert.Equal(package.Identity, (await restarted.ResolveAsync(package.Identity, default)).Identity);
        Assert.Equal(package.Identity, (await fixture.InstallAsync()).Identity);
        Assert.Single(await restarted.GetProvidersAsync(default));
        await File.AppendAllTextAsync(Path.Combine(package.ArtifactPath, package.Manifest.EntryPoint), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.ResolveAsync(package.Identity, default));
    }
    [Fact]
    public async Task WrongDigestPublisherSignatureAndTraversalCannotInstallOrChangeTrustedNeighbor()
    {
        await using var fixture = await Fixture.CreateAsync();
        var trusted = await fixture.InstallAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.InstallAsync(new(fixture.Archive, new string('0', 64)), default));
        var bad = await fixture.CreateArchiveAsync("2.0.0", invalidateSignature: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.InstallAsync(bad, default));
        var traversal = await fixture.CreateArchiveAsync("3.0.0", unsafePath: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.InstallAsync(traversal, default));
        Assert.Single(await fixture.Store.GetProvidersAsync(default));
        Assert.Equal(trusted.Identity, (await fixture.Store.ResolveAsync(trusted.Identity, default)).Identity);
    }
    [Fact]
    public async Task CatalogSelectsSemanticLatestAndKeepsExplicitOlderAndPrereleaseVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var versions = new[] { "1.9.0", "1.10.0-beta.2", "1.10.0-beta.11", "1.10.0-rc.1", "1.10.0", "1.10.0+build.1" };
        foreach (var version in versions) await fixture.Store.InstallAsync(await fixture.CreateArchiveAsync(version), default);
        Assert.Equal("1.10.0+build.1", (await fixture.Store.GetAsync("test.provider", null, default)).PackageVersion);
        Assert.Equal("1.10.0+build.1", Assert.Single(await fixture.Store.GetProvidersAsync(default)).PackageVersion);
        Assert.Equal(versions.Reverse(), (await fixture.Store.GetVersionsAsync("test.provider", default)).Select(package => package.PackageVersion));
        Assert.Equal("1.9.0", (await fixture.Store.GetAsync("test.provider", "1.9.0", default)).PackageVersion);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.InstallAsync(await fixture.CreateArchiveAsync("01.0.0"), default));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.InstallAsync(await fixture.CreateArchiveAsync("2.0.0-beta.01"), default));
        Assert.Equal(versions.Length, (await fixture.Store.GetVersionsAsync("test.provider", default)).Count);
    }
    [Fact]
    public async Task CatalogForgeryCannotReplaceAuthenticatedManifest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var package = await fixture.InstallAsync();
        var changed = package with { Manifest = package.Manifest with { EntryPoint = "forged.dll" } };
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.PackageDirectory, "catalog.json"), JsonSerializer.Serialize(new[] { changed }, IntegrationJson.Options));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ResolveAsync(package.Identity, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.GetProvidersAsync(default));
    }
    [Fact]
    public async Task SignedLayoutInstallsDynamicallyAndMalformedReferencesCannotChangeTrustedCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var flat = await fixture.InstallAsync();
        var descriptor = DescriptorTests.Wizard() with { PackageVersion = "2.0.0" };
        var installed = await fixture.Store.InstallAsync(await fixture.CreateArchiveAsync("2.0.0", descriptorOverride: descriptor), default);
        var described = await fixture.Store.GetAsync("test.provider", "2.0.0", default);
        Assert.Equal(1, described.UiLayout!.Version);
        Assert.Equal("apiKey", Assert.Single(described.OAuthDefinition!.SecretFieldKeys));
        Assert.NotEqual((await fixture.Store.GetAsync("test.provider", "1.0.0", default)).DescriptorDigest, described.DescriptorDigest);
        var malformed = descriptor with { PackageVersion = "3.0.0", UiLayout = new(1, [new("bad", "Bad", null, [new("bad", "Bad", null, ["foreign"], [])])]) };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Store.InstallAsync(await fixture.CreateArchiveAsync("3.0.0", descriptorOverride: malformed), default));
        Assert.Equal(new[] { installed.Identity, flat.Identity }, (await fixture.Store.GetVersionsAsync("test.provider", default)).Select(value => new ProviderPackageIdentity(value.ProviderId, value.PackageVersion, value.PackageDigest)));
    }
    [Fact]
    public async Task OAuthUsesIsolatedWorkersAndOnlyDeclaredOutputsWithoutChangingActiveAccounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.ApprovedOrigins.Add("https://oauth.example.test");
        var flat = await fixture.InstallAsync();
        var package = await fixture.Store.InstallAsync(await fixture.CreateArchiveAsync("2.0.0", descriptorOverride: DescriptorTests.Wizard() with { PackageVersion = "2.0.0" }, origins: ["https://oauth.example.test"]), default);
        await using var runtime = fixture.Runtime();
        var active = fixture.Session(flat.Identity, "two");
        Assert.Equal(1m, (await ReadAsync(runtime, active)).BatterySoc.Value);
        var verifier = new string('v', 64);
        var begin = new IntegrationOAuthBeginRequest("https://host.example.test/api/v2/integrations/oauth/callback", new string('s', 43), IntegrationOAuthProtocol.Challenge(verifier));
        var authorization = await runtime.BeginAuthorizationAsync(package.Identity, Fixture.Draft("one"), begin, default);
        IntegrationOAuthProtocol.ValidateAuthorizationUrl(authorization.AuthorizationUrl, begin, ["https://oauth.example.test"]);
        var complete = new IntegrationOAuthCompleteRequest("fixture-code:" + begin.CodeChallenge, begin.RedirectUri, verifier);
        var result = await runtime.CompleteAuthorizationAsync(package.Identity, Fixture.Draft("one"), complete, default);
        Assert.True(result.Success);
        Assert.Equal("one", result.AccountIdentity);
        Assert.Equal("one", result.PublicValues.GetProperty("account").GetString());
        Assert.Equal("fixture-oauth-token:one", Assert.Single(result.SecretValues).Value);
        Assert.False(result.PublicValues.TryGetProperty("apiKey", out _));
        var denied = await runtime.CompleteAuthorizationAsync(package.Identity, Fixture.Draft("one"), complete with { CodeVerifier = new string('x', 64) }, default);
        Assert.False(denied.Success);
        Assert.Empty(denied.SecretValues);
        Assert.Empty(denied.PublicValues.EnumerateObject());
        foreach (var values in new[] { IntegrationJson.Element(new { account = "one", oauthExtraSecret = true }), IntegrationJson.Element(new { account = "one", oauthPublicSecret = true }) })
            await Assert.ThrowsAsync<InvalidDataException>(() => runtime.CompleteAuthorizationAsync(package.Identity, new(values, new Dictionary<string, string>()), complete, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.BeginAuthorizationAsync(package.Identity,
            new(IntegrationJson.Element(new { account = "one", oauthForeignOrigin = true }), new Dictionary<string, string>()), begin, default));
        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.BeginAuthorizationAsync(flat.Identity, Fixture.Draft("one"), begin, default));
        var unchanged = await ReadAsync(runtime, active);
        Assert.Equal(2m, unchanged.BatterySoc.Value);
        Assert.Equal(2200m, unchanged.LoadPower.Value);
    }
    [Fact]
    public async Task OperatorOriginApprovalIsAdditiveDurableAndRequiredBeforeInstallingNewProviderNetworkPermission()
    {
        await using var fixture = await Fixture.CreateAsync();
        var policy = new OriginPolicyStore();
        var store = new IntegrationPackageStore(Options.Create(fixture.Options), policy);
        var neighbor = await store.InstallAsync(new(fixture.Archive, (await fixture.InstallAsync()).Identity.PackageDigest), default);
        var request = await fixture.CreateArchiveAsync("2.0.0", origins: ["https://oauth.example.test"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(request, default));
        Assert.Empty(policy.Origins);
        Assert.Single(await store.GetProvidersAsync(default));
        await store.ApproveOriginAsync("https://OAUTH.example.test:443/", default);
        await store.ApproveOriginAsync("https://oauth.example.test", default);
        Assert.Equal(new[] { "https://oauth.example.test" }, policy.Origins);
        Assert.Equal(1, policy.Saves);
        var installed = await store.InstallAsync(request, default);
        var restarted = new IntegrationPackageStore(Options.Create(fixture.Options), policy);
        Assert.Equal(installed.Identity, (await restarted.ResolveAsync(installed.Identity, default)).Identity);
        Assert.Equal(neighbor.Identity, (await restarted.ResolveAsync(neighbor.Identity, default)).Identity);
        policy.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => restarted.ApproveOriginAsync("https://second.example.test", default));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await restarted.InstallAsync(await fixture.CreateArchiveAsync("3.0.0", origins: ["https://second.example.test"]), default));
        Assert.Equal(new[] { "https://oauth.example.test" }, policy.Origins);
        Assert.Equal(2, (await restarted.GetVersionsAsync("test.provider", default)).Count);
    }
    [Fact]
    public async Task CorruptOrExcessivePersistedOriginPolicyDoesNotGrantNetworkPermissions()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var values in new[] { new[] { "https://*.example.test" }, new[] { "https://oauth.example.test", "https://oauth.example.test" }, Enumerable.Range(0, 129).Select(index => "https://host" + index + ".example.test").ToArray() })
        {
            var store = new IntegrationPackageStore(Options.Create(fixture.Options), new OriginPolicyStore { Origins = values });
            await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(new(fixture.Archive, new string('0', 64)), default));
        }
    }
    [Fact]
    public async Task ActiveAndEphemeralSessionsKeepIndependentAccountsAndDoNotInheritHostSecrets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var first = fixture.Session(package.Identity, "one");
        var neighbor = fixture.Session(package.Identity, "two");
        var before = await ReadAsync(runtime, first);
        var other = await ReadAsync(runtime, neighbor);
        var previous = Environment.GetEnvironmentVariable("SOLAR_WORKER_SECRET_TEST");
        IntegrationTestResult result;
        try
        {
            Environment.SetEnvironmentVariable("SOLAR_WORKER_SECRET_TEST", "must-not-inherit");
            result = await runtime.TestAsync(package.Identity, Fixture.Draft("draft"), default);
        }
        finally { Environment.SetEnvironmentVariable("SOLAR_WORKER_SECRET_TEST", previous); }
        Assert.True(result.Success);
        Assert.Equal("draft", result.AccountIdentity);
        var after = await ReadAsync(runtime, first);
        Assert.Equal(1m, before.BatterySoc.Value);
        Assert.Equal(2m, after.BatterySoc.Value);
        Assert.Equal(1100m, after.LoadPower.Value);
        Assert.Equal(2200m, other.LoadPower.Value);
    }
    [Fact]
    public async Task ReplacementFencesOldGenerationEvenAfterWorkerEvictionAndDisable()
    {
        await using var fixture = await Fixture.CreateAsync(maximumWorkers: 1);
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var old = fixture.Session(package.Identity, "one");
        await ReadAsync(runtime, old);
        var replacement = old with { Generation = 2, ConfigurationRevision = 2, Configuration = Fixture.Draft("two") };
        Assert.Equal(2200m, (await ReadAsync(runtime, replacement)).LoadPower.Value);
        await ReadAsync(runtime, fixture.Session(package.Identity, "one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, old));
        await runtime.StopAsync(replacement.InstanceId, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, replacement));
    }
    [Fact]
    public async Task RevisionAndGenerationCannotBeReusedForChangedCredentialsOrPackage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var session = fixture.Session(package.Identity, "one");
        await ReadAsync(runtime, session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, session with { ConfigurationRevision = 2, Configuration = Fixture.Draft("two") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, session with { Generation = 2, Configuration = Fixture.Draft("two") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, session with { Package = package.Identity with { PackageVersion = "other" } }));
        Assert.Equal(1100m, (await ReadAsync(runtime, session)).LoadPower.Value);
    }
    [Fact]
    public async Task FailedReplacementRetainsNewFenceAndDoesNotFallBackToOldCredentials()
    {
        await using var fixture = await Fixture.CreateAsync();
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var session = fixture.Session(package.Identity, "one");
        await ReadAsync(runtime, session);
        var replacement = session with
        {
            Generation = 2,
            ConfigurationRevision = 2,
            Package = package.Identity with { PackageVersion = "uninstalled" },
            Configuration = Fixture.Draft("two")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, replacement));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, session));
        Assert.Contains("replaced or disabled", failure.Message);
        Assert.Equal(2200m, (await ReadAsync(runtime, fixture.Session(package.Identity, "two"))).LoadPower.Value);
    }
    [Fact]
    public async Task BoundedIdentityAdmissionKeepsExistingFenceInsteadOfForgettingIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Options.MaximumRememberedInstances = 1;
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var session = fixture.Session(package.Identity, "one");
        await ReadAsync(runtime, session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, fixture.Session(package.Identity, "two")));
        Assert.Equal(1100m, (await ReadAsync(runtime, session)).LoadPower.Value);
    }
    [Fact]
    public async Task TimeoutEndsOnlyFailedSessionAndNeighborRemainsUsable()
    {
        await using var fixture = await Fixture.CreateAsync(requestTimeoutSeconds: 1);
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var failed = fixture.Session(package.Identity, "one");
        var neighbor = fixture.Session(package.Identity, "two");
        await Assert.ThrowsAsync<TimeoutException>(() => ReadAsync(runtime, failed, "hang"));
        Assert.Equal(2200m, (await ReadAsync(runtime, neighbor)).LoadPower.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, failed));
    }
    [Fact]
    public async Task SignedWorkerCanRequestBoundedLongerDeadlineAndCannotExceedOperatorCap()
    {
        await using var fixture = await Fixture.CreateAsync(requestTimeoutSeconds: 1);
        fixture.Options.MaximumNegotiatedRequestTimeoutSeconds = 2;
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var session = fixture.Session(package.Identity, "one") with
        {
            Configuration = new(
            IntegrationJson.Element(new { account = "one", minimumOperationTimeoutSeconds = 2 }), new Dictionary<string, string>())
        };
        Assert.Equal(1100m, (await ReadAsync(runtime, session, "negotiated-delay")).LoadPower.Value);
        var excessive = fixture.Session(package.Identity, "two") with
        {
            Configuration = new(
            IntegrationJson.Element(new { account = "two", minimumOperationTimeoutSeconds = 3 }), new Dictionary<string, string>())
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, excessive));
        Assert.Equal(1100m, (await ReadAsync(runtime, session)).LoadPower.Value);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingCommandsPreventIdleEvictionUntilTheirTerminalReceipt(bool lowercaseStatus)
    {
        await using var fixture = await Fixture.CreateAsync(maximumWorkers: 1);
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var session = fixture.Session(package.Identity, "one");
        var id = Guid.NewGuid().ToString();
        var pending = await runtime.InvokeAsync(session, "socket.set", IntegrationJson.Element(new { commandId = id, lowercaseStatus }), default);
        Assert.Equal(lowercaseStatus ? "pending" : "Pending", pending.GetProperty("status").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, fixture.Session(package.Identity, "two")));
        await runtime.InvokeAsync(session, "socket.result", IntegrationJson.Element(new { commandId = id }), default);
        Assert.Equal(2200m, (await ReadAsync(runtime, fixture.Session(package.Identity, "two"))).LoadPower.Value);
    }
    [Fact]
    public async Task ForeignCommandReceiptEndsSessionWithoutPinningUnrelatedTracking()
    {
        await using var fixture = await Fixture.CreateAsync(maximumWorkers: 1);
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.InvokeAsync(fixture.Session(package.Identity, "one"), "socket.set",
            IntegrationJson.Element(new { commandId = Guid.NewGuid().ToString("D"), mismatchId = true }), default));
        Assert.Equal(2200m, (await ReadAsync(runtime, fixture.Session(package.Identity, "two"))).LoadPower.Value);
    }
    [Fact]
    public async Task DurableUncertainCloseCanReleaseTrackingWithoutReplayingACommand()
    {
        await using var fixture = await Fixture.CreateAsync(maximumWorkers: 1);
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var session = fixture.Session(package.Identity, "one");
        var command = Guid.NewGuid();
        await runtime.InvokeAsync(session, "socket.set", IntegrationJson.Element(new { commandId = command.ToString("D"), isOn = true }), default);
        await runtime.ReleaseCommandTrackingAsync(session.InstanceId, Guid.NewGuid(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(runtime, fixture.Session(package.Identity, "two")));
        await runtime.ReleaseCommandTrackingAsync(session.InstanceId, command, default);
        Assert.Equal(2200m, (await ReadAsync(runtime, fixture.Session(package.Identity, "two"))).LoadPower.Value);
    }
    [Fact]
    public async Task FramesRejectOversizeTruncationAndInvalidJson()
    {
        using var oversized = new MemoryStream(new byte[] { 0, 16, 0, 0 });
        await Assert.ThrowsAsync<InvalidDataException>(() => LengthFramedJson.ReadAsync(oversized, 1024, default));
        using var truncated = new MemoryStream(new byte[] { 0, 0, 0, 10, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => LengthFramedJson.ReadAsync(truncated, 1024, default));
        using var invalid = new MemoryStream(new byte[] { 0, 0, 0, 1, 123 });
        await Assert.ThrowsAnyAsync<JsonException>(() => LengthFramedJson.ReadAsync(invalid, 1024, default));
        using var roundtrip = new MemoryStream();
        await LengthFramedJson.WriteAsync(roundtrip, new { id = 3, result = "ok" }, 1024, default);
        roundtrip.Position = 0;
        Assert.Equal("ok", (await LengthFramedJson.ReadAsync(roundtrip, 1024, default))!.Value.GetProperty("result").GetString());
    }
    [Fact]
    public async Task IndependentInstancesRunWhileNeighborIsWaitingAndCancellationDrainsIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var package = await fixture.InstallAsync();
        await using var runtime = fixture.Runtime();
        var startedPath = Path.Combine(fixture.DirectoryPath, "started");
        var releasePath = Path.Combine(fixture.DirectoryPath, "release");
        using var cancel = new CancellationTokenSource();
        var pending = runtime.InvokeAsync(fixture.Session(package.Identity, "one"), "inverter.read",
            IntegrationJson.Element(new { remoteId = "barrier", startedPath, releasePath }), cancel.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(startedPath)) await Task.Delay(10, deadline.Token);
        Assert.Equal(2200m, (await ReadAsync(runtime, fixture.Session(package.Identity, "two"))).LoadPower.Value);
        Assert.False(pending.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("192.0.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("2001::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public void CloudNetworkPolicyRejectsLocalAndMetadataAddresses(string address, bool expected)
        => Assert.Equal(expected, CloudHttpClient.IsPublic(System.Net.IPAddress.Parse(address)));
    private static async Task<ProviderInverterTelemetry> ReadAsync(IntegrationWorkerRuntime runtime, IntegrationSession session, string remoteId = "same-id")
        => (await runtime.InvokeAsync(session, "inverter.read", IntegrationJson.Element(new { remoteId }), default))
            .Deserialize<ProviderInverterTelemetry>(IntegrationJson.Options)!;
    private static async Task<string> RunToolAsync(string tool, params string[] args)
        => await RunToolAsync(tool, true, args);
    private static async Task<string> RunToolAsync(string tool, bool expectedSuccess, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(tool);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True((process.ExitCode == 0) == expectedSuccess, await errors);
        return await output;
    }
    private sealed class OriginPolicyStore : IIntegrationOriginPolicyStore
    {
        public IReadOnlyList<string> Origins = [];
        public bool FailSave;
        public int Saves;
        public Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Origins); }
        public Task SaveAsync(IReadOnlyList<string> origins, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailSave) throw new IOException("Injected policy persistence failure.");
            Origins = origins.ToArray();
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-integration-test-" + Guid.NewGuid().ToString("N"));
        private readonly RSA _key = RSA.Create(2048);
        public string DirectoryPath => _root;
        public IntegrationRuntimeOptions Options { get; private set; } = null!;
        public IntegrationPackageStore Store { get; private set; } = null!;
        public string Archive { get; private set; } = null!;
        private string Digest { get; set; } = null!;
        public static async Task<Fixture> CreateAsync(int maximumWorkers = 4, int requestTimeoutSeconds = 5)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture._root);
            fixture.Options = new()
            {
                PackageDirectory = Path.Combine(fixture._root, "store"),
                MaximumWorkers = maximumWorkers,
                RequestTimeoutSeconds = requestTimeoutSeconds,
                TrustedPublisherPublicKeys = new() { ["test-publisher"] = fixture._key.ExportSubjectPublicKeyInfoPem() }
            };
            fixture.Store = new IntegrationPackageStore(Microsoft.Extensions.Options.Options.Create(fixture.Options));
            var archive = await fixture.CreateArchiveAsync("1.0.0");
            fixture.Archive = archive.ArchivePath;
            fixture.Digest = archive.ExpectedSha256;
            return fixture;
        }
        public Task<IntegrationInstalledPackage> InstallAsync() => Store.InstallAsync(new(Archive, Digest), default);
        public IntegrationWorkerRuntime Runtime() => new(Store, Microsoft.Extensions.Options.Options.Create(Options));
        public IntegrationSession Session(ProviderPackageIdentity package, string account) => new("installation-one", Guid.NewGuid(), package, 1, 1, Draft(account));
        public static IntegrationDraftConfiguration Draft(string account) => new(IntegrationJson.Element(new { account }), new Dictionary<string, string>());
        public async Task<IntegrationPackageInstallRequest> CreateArchiveAsync(string version, bool invalidateSignature = false, bool unsafePath = false,
            IntegrationProviderDescriptor? descriptorOverride = null, IReadOnlyList<string>? origins = null)
        {
            var payload = Path.Combine(AppContext.BaseDirectory, "worker");
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                await using var input = File.OpenRead(path);
                files[Path.GetRelativePath(payload, path).Replace('\\', '/')] = Convert.ToHexString(await SHA256.HashDataAsync(input));
            }
            var descriptor = new IntegrationProviderDescriptor("test.provider", version, "", "", "Unknown test provider", 1, 1, ["text"],
                [new("account", "text", "Account", true)], ["test", "discover"]);
            var manifest = new IntegrationPackageManifest("test.provider", version, "test-publisher", 1, "portable",
                "SolarManagement.IntegrationTestWorker.dll", files, origins ?? [], descriptorOverride ?? descriptor);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, IntegrationJson.Options);
            var signature = _key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            if (invalidateSignature) signature[0] ^= 1;
            var archivePath = Path.Combine(_root, version + ".zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                foreach (var relative in files.Keys) archive.CreateEntryFromFile(Path.Combine(payload, relative), relative);
                await using (var output = archive.CreateEntry("manifest.json").Open()) await output.WriteAsync(bytes);
                await using (var output = archive.CreateEntry("manifest.signature").Open()) await output.WriteAsync(Encoding.UTF8.GetBytes(Convert.ToBase64String(signature)));
                if (unsafePath) archive.CreateEntry("../escaped.txt");
            }
            await using var package = File.OpenRead(archivePath);
            return new(archivePath, Convert.ToHexString(await SHA256.HashDataAsync(package)));
        }
        public ValueTask DisposeAsync()
        {
            _key.Dispose();
            var full = Path.GetFullPath(_root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("solar-integration-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its task directory.");
            Directory.Delete(full, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
