using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Integrations;

/// <summary>The operator's additive approvals have integrity protection and survive package installation and restart.</summary>
public sealed class IntegrationOriginPolicyFileStore(IntegrationSecretStore secrets, IOptions<IntegrationRuntimeOptions> options)
    : IIntegrationOriginPolicyStore
{
    private string Pathname => Path.Combine(Path.GetFullPath(options.Value.PackageDirectory), "operator-origins.protected");
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct)
    {
        var path = Pathname;
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The approved origin policy is invalid.");
        try
        {
            var ciphertext = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
            var origins = JsonSerializer.Deserialize<string[]>(secrets.UnprotectOperatorOrigins(ciphertext), IntegrationJson.Options);
            if (origins is null || origins.Length > 128 || origins.Any(origin => origin is null || origin.Length > 2048)) throw new InvalidDataException();
            ValidateOrigins(origins);
            return origins;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        { throw new InvalidDataException("The approved origin policy cannot be authenticated. Restore its encryption keys and protected file.", ex); }
    }
    public async Task SaveAsync(IReadOnlyList<string> origins, CancellationToken ct)
    {
        if (origins.Count > 128 || origins.Any(origin => origin is null || origin.Length > 2048)) throw new InvalidDataException("The approved origin policy is invalid.");
        ValidateOrigins(origins);
        var path = Pathname;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(secrets.ProtectOperatorOrigins(JsonSerializer.Serialize(origins, IntegrationJson.Options)));
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void ValidateOrigins(IReadOnlyList<string> origins)
    {
        try
        {
            if (origins.Distinct(StringComparer.Ordinal).Count() != origins.Count
                || origins.Any(origin => IntegrationOriginPolicy.NormalizeExactOrigin(origin) != origin)) throw new InvalidDataException("The approved origin policy is not canonical.");
        }
        catch (ArgumentException ex) { throw new InvalidDataException("The approved origin policy contains an invalid origin.", ex); }
    }
}
