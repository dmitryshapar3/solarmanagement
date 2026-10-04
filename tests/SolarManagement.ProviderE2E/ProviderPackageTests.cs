using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace SolarManagement.ProviderE2E;

public class ProviderPackageTests
{
    [Theory]
    [InlineData("SolisCloud", "inverter")]
    [InlineData("SungrowCloud", "inverter")]
    [InlineData("HuaweiFusionSolar", "inverter")]
    [InlineData("GrowattCloud", "inverter")]
    [InlineData("TuyaCloud", "socket")]
    [InlineData("EWeLinkCloud", "socket")]
    [InlineData("AqaraCloud", "socket")]
    [InlineData("NetatmoControl", "socket")]
    public async Task ProviderDescriptorAndSignedPackageCanBeAdmitted(string suffix, string kind)
    {
        var root = RepositoryRoot();
        var templatePath = Path.Combine(root, "integrations", "SolarManagement.Providers." + suffix, "manifest.json");
        var manifest = JsonSerializer.Deserialize<IntegrationPackageManifest>(await File.ReadAllTextAsync(templatePath), IntegrationJson.Options)!;
        IntegrationDescriptorValidator.Validate(manifest.Descriptor);
        Assert.Equal(manifest.ProviderId, manifest.Descriptor.ProviderId);
        Assert.Equal(manifest.PackageVersion, manifest.Descriptor.PackageVersion);
        Assert.Equal("SolarManagement.Providers." + suffix + ".dll", manifest.EntryPoint);
        Assert.Contains(manifest.Descriptor.UiLayout!.Steps, step => step.Id == kind);
        Assert.Contains("test", manifest.Descriptor.Actions);
        Assert.Contains("discover", manifest.Descriptor.Actions);
        Assert.NotEmpty(manifest.AllowedOrigins);
        Assert.All(manifest.AllowedOrigins, origin => Assert.StartsWith("https://", origin));
        Assert.All(manifest.Descriptor.Fields.Where(field => field.Secret || field.Kind == "secret"), field => Assert.Null(field.DefaultValue));

        var temporary = Path.Combine(Path.GetTempPath(), "solar-provider-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            using var publisher = RSA.Create(2048);
            var privatePath = Path.Combine(temporary, "fixture-publisher-private.pem");
            await File.WriteAllTextAsync(privatePath, publisher.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var archivePath = Path.Combine(temporary, "provider.zip");
            // The launcher copy contains each production provider assembly and all of
            // its dependencies, exercising the real packager without rebuilding in tests.
            var payloadPath = Path.Combine(AppContext.BaseDirectory, "worker");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "packager", "SolarManagement.IntegrationPackager.dll"),
                         payloadPath, templatePath, privatePath, archivePath }) start.ArgumentList.Add(argument);
            using var packager = Process.Start(start)!;
            var output = packager.StandardOutput.ReadToEndAsync();
            var error = packager.StandardError.ReadToEndAsync();
            await packager.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(packager.ExitCode == 0, "Packager rejected provider manifest: " + await error);
            var digest = (await output).Trim();
            var store = new IntegrationPackageStore(Options.Create(new IntegrationRuntimeOptions
            {
                PackageDirectory = Path.Combine(temporary, "installed"),
                TrustedPublisherPublicKeys = new Dictionary<string, string> { [manifest.PublisherId] = publisher.ExportSubjectPublicKeyInfoPem() },
                ApprovedOrigins = manifest.AllowedOrigins.ToList()
            }));
            var installed = await store.InstallAsync(new(archivePath, digest), CancellationToken.None);
            Assert.Equal(manifest.ProviderId, installed.Identity.ProviderId);
            Assert.Contains(manifest.EntryPoint, installed.Manifest.Files.Keys);
            Assert.DoesNotContain(installed.Manifest.Files.Keys, path => path.Contains("private", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(installed.Identity, (await store.ResolveAsync(installed.Identity, CancellationToken.None)).Identity);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeyeSolar.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot locate provider manifest templates.");
    }
}
