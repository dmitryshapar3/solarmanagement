using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Integrations.Runtime;

public sealed class IntegrationPackageStore : IIntegrationPackageManager, IIntegrationProviderCatalog
{
    private readonly IntegrationRuntimeOptions _options;
    private readonly Dictionary<string, string> _publisherKeys;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IntegrationPackageStore(IOptions<IntegrationRuntimeOptions> options)
    {
        _options = options.Value;
        _options.Validate();
        _publisherKeys = new(_options.TrustedPublisherPublicKeys, StringComparer.Ordinal);
        foreach (var (publisher, path) in _options.TrustedPublisherPublicKeyFiles)
        {
            if (_publisherKeys.ContainsKey(publisher)) throw new ArgumentException("Publisher trust has conflicting key sources.");
            if (new FileInfo(path).Length > 16384) throw new ArgumentException("Publisher public key file is too large.");
            _publisherKeys[publisher] = File.ReadAllText(path);
        }
        _root = Path.GetFullPath(_options.PackageDirectory);
        Directory.CreateDirectory(_root);
    }
    public async Task<IntegrationInstalledPackage> InstallAsync(IntegrationPackageInstallRequest request, CancellationToken ct)
    {
        if (!IsDigest(request.ExpectedSha256)) throw new ArgumentException("An exact package SHA256 is required.");
        await _gate.WaitAsync(ct);
        string? staging = null;
        try
        {
            await using var source = File.OpenRead(request.ArchivePath);
            if (source.Length > 64 * 1024 * 1024) throw new InvalidDataException("Package archive is too large.");
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
            if (!digest.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package digest does not match the authorized artifact.");
            source.Position = 0;
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is < 3 or > 256 || archive.Entries.Sum(entry => entry.Length) > 256L * 1024 * 1024)
                throw new InvalidDataException("Package archive exceeds its extraction limits.");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                ValidateRelative(entry.FullName);
                if (!entries.TryAdd(entry.FullName, entry) || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                    throw new InvalidDataException("Duplicate paths and symlinks are not permitted.");
            }
            if (!entries.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length > 65536
                || !entries.TryGetValue("manifest.signature", out var signatureEntry) || signatureEntry.Length > 4096)
                throw new InvalidDataException("Signed package manifest is missing or too large.");
            var bytes = await ReadBytesAsync(manifestEntry, ct);
            var manifest = JsonSerializer.Deserialize<IntegrationPackageManifest>(bytes, IntegrationJson.Options)
                ?? throw new InvalidDataException("Package manifest is invalid.");
            ValidateManifest(manifest);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_publisherKeys[manifest.PublisherId]);
            var signature = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(signatureEntry, ct)));
            if (!rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidDataException("Package publisher signature is invalid.");
            if (manifest.Files.Count != entries.Count - 2 || !manifest.Files.ContainsKey(manifest.EntryPoint))
                throw new InvalidDataException("Every package payload must be covered by the signed manifest.");
            var catalog = await ReadCatalogAsync(ct);
            var existing = catalog.FirstOrDefault(package => package.Identity.ProviderId == manifest.ProviderId
                && package.Identity.PackageVersion == manifest.PackageVersion);
            if (existing is not null)
            {
                if (existing.Identity.PackageDigest != digest) throw new InvalidDataException("A package version is immutable.");
                await VerifyInstalledAsync(existing, ct);
                return existing;
            }
            staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.json"), bytes, ct);
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.signature"), await ReadBytesAsync(signatureEntry, ct), ct);
            foreach (var (relative, expected) in manifest.Files)
            {
                ValidateRelative(relative);
                if (!IsDigest(expected) || !entries.TryGetValue(relative, out var entry))
                    throw new InvalidDataException("Package payload does not match the signed manifest.");
                var destination = Within(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var output = File.Create(destination))
                await using (var input = entry.Open()) await input.CopyToAsync(output, ct);
                await using var read = File.OpenRead(destination);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(read, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package payload digest is invalid.");
            }
            var artifact = Path.Combine(_root, digest);
            var installed = new IntegrationInstalledPackage(new(manifest.ProviderId, manifest.PackageVersion, digest),
                artifact, manifest, DateTimeOffset.UtcNow);
            if (Directory.Exists(artifact))
            {
                // Recover an interrupted catalog commit only after verifying the immutable artifact.
                await VerifyInstalledAsync(installed, ct);
                Directory.Delete(Within(_root, Path.GetFileName(staging)), recursive: true);
            }
            else await PublishDirectoryAsync(staging, artifact, ct);
            staging = null;
            catalog.Add(installed);
            await WriteCatalogAsync(catalog, ct);
            return installed;
        }
        finally
        {
            if (staging is not null) Directory.Delete(Within(_root, Path.GetFileName(staging)), recursive: true);
            _gate.Release();
        }
    }
    public async Task<IntegrationInstalledPackage> ResolveAsync(ProviderPackageIdentity identity, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var package = (await ReadCatalogAsync(ct)).SingleOrDefault(package => package.Identity == identity)
                ?? throw new InvalidOperationException("The exact trusted package version is not installed.");
            await VerifyInstalledAsync(package, ct);
            return package;
        }
        finally { _gate.Release(); }
    }
    private async Task<IReadOnlyList<IntegrationInstalledPackage>> GetPackagesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var packages = await ReadCatalogAsync(ct);
            foreach (var package in packages) await VerifyInstalledAsync(package, ct);
            return packages;
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct)
        => (await GetPackagesAsync(ct)).GroupBy(package => package.Identity.ProviderId, StringComparer.Ordinal)
            .Select(group => Newest(group).First()).Select(Describe).OrderBy(item => item.ProviderId, StringComparer.Ordinal).ToArray();
    public async Task<IntegrationProviderDescriptor> GetAsync(string providerId, string? packageVersion, CancellationToken ct)
    {
        var packages = await GetPackagesAsync(ct);
        var candidates = Newest(packages.Where(item => item.Identity.ProviderId == providerId
            && (packageVersion is null || item.Identity.PackageVersion == packageVersion))).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Choose an installed provider and package version.");
        return Describe(candidates[0]);
    }
    public async Task<IReadOnlyList<IntegrationProviderDescriptor>> GetVersionsAsync(string providerId, CancellationToken ct)
        => Newest((await GetPackagesAsync(ct)).Where(package => package.Identity.ProviderId == providerId)).Select(Describe).ToArray();
    private static IOrderedEnumerable<IntegrationInstalledPackage> Newest(IEnumerable<IntegrationInstalledPackage> packages)
        => packages.OrderByDescending(package => SemanticPackageVersion.Parse(package.Identity.PackageVersion))
            .ThenByDescending(package => package.Identity.PackageVersion, StringComparer.Ordinal);
    private static IntegrationProviderDescriptor Describe(IntegrationInstalledPackage package)
    {
        var descriptor = package.Manifest.Descriptor;
        var descriptorDigest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(descriptor, IntegrationJson.Options)));
        return descriptor with { PackageDigest = package.Identity.PackageDigest, DescriptorDigest = descriptorDigest };
    }
    private void ValidateManifest(IntegrationPackageManifest manifest)
    {
        if (!ValidIdentifier(manifest.ProviderId) || !SemanticPackageVersion.TryParse(manifest.PackageVersion, out _)
            || manifest.WireVersion != 1 || !_publisherKeys.ContainsKey(manifest.PublisherId)
            || manifest.Descriptor.ProviderId != manifest.ProviderId || manifest.Descriptor.PackageVersion != manifest.PackageVersion
            || manifest.Descriptor.UiContractVersion != 1 || manifest.Descriptor.ConfigurationVersion < 1
            || !manifest.EntryPoint.EndsWith(".dll", StringComparison.Ordinal)
            || manifest.AllowedOrigins.Any(origin => !_options.ApprovedOrigins.Contains(origin, StringComparer.Ordinal)))
            throw new InvalidDataException("Package identity, publisher, contract or network permission is not supported.");
        ValidateRelative(manifest.EntryPoint);
        if (manifest.RuntimeIdentifier != "portable" && manifest.RuntimeIdentifier != System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)
            throw new InvalidDataException("The package runtime does not match this host.");
    }
    private async Task VerifyInstalledAsync(IntegrationInstalledPackage package, CancellationToken ct)
    {
        ValidateManifest(package.Manifest);
        if (package.Identity.ProviderId != package.Manifest.ProviderId || package.Identity.PackageVersion != package.Manifest.PackageVersion
            || !IsDigest(package.Identity.PackageDigest)) throw new InvalidDataException("Package identity does not match its manifest.");
        if (Path.GetFullPath(package.ArtifactPath) != Within(_root, package.Identity.PackageDigest))
            throw new InvalidDataException("Package catalog escaped the configured artifact store.");
        var bytes = await File.ReadAllBytesAsync(Within(package.ArtifactPath, "manifest.json"), ct);
        var signed = JsonSerializer.Deserialize<IntegrationPackageManifest>(bytes, IntegrationJson.Options)
            ?? throw new InvalidDataException("Installed manifest is invalid.");
        var signature = Convert.FromBase64String(await File.ReadAllTextAsync(Within(package.ArtifactPath, "manifest.signature"), ct));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_publisherKeys[package.Manifest.PublisherId]);
        if (!rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
            || JsonSerializer.Serialize(signed, IntegrationJson.Options) != JsonSerializer.Serialize(package.Manifest, IntegrationJson.Options))
            throw new InvalidDataException("Installed manifest authenticity does not match the package catalog.");
        foreach (var (relative, expected) in package.Manifest.Files)
        {
            await using var input = File.OpenRead(Within(package.ArtifactPath, relative));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An installed package was modified.");
        }
    }
    private async Task<List<IntegrationInstalledPackage>> ReadCatalogAsync(CancellationToken ct)
    {
        var path = Path.Combine(_root, "catalog.json");
        if (!File.Exists(path)) return [];
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<IntegrationInstalledPackage>>(input, IntegrationJson.Options, ct)
            ?? throw new InvalidDataException("Package catalog is invalid.");
    }
    private async Task WriteCatalogAsync(List<IntegrationInstalledPackage> catalog, CancellationToken ct)
    {
        var temporary = Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(output, catalog, IntegrationJson.Options, ct); await output.FlushAsync(ct); output.Flush(flushToDisk: true); }
            File.Move(temporary, Path.Combine(_root, "catalog.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task PublishDirectoryAsync(string staging, string artifact, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(staging, artifact); return; }
            catch (IOException ex) when (OperatingSystem.IsWindows() && attempt < 3 && (ex.HResult & 0xffff) is 5 or 32 or 33)
            {
                // Executable payloads can be briefly held by Windows security scanners after their streams close.
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
            }
        }
    }
    private static async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry, CancellationToken ct)
    { await using var input = entry.Open(); using var memory = new MemoryStream(); await input.CopyToAsync(memory, ct); return memory.ToArray(); }
    internal static bool IsDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool ValidIdentifier(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    internal static string Within(string root, string relative)
    {
        ValidateRelative(relative);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Package path escaped its store.");
        return full;
    }
    private static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512 || Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Unsafe package path.");
    }
}
