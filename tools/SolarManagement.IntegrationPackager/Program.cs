using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

if (args.Length == 3 && args[0] == "--generate-key")
{
    if (Path.GetFullPath(args[1]) == Path.GetFullPath(args[2])) throw new IOException("Private and public key paths must differ.");
    if (File.Exists(args[1]) || File.Exists(args[2])) throw new IOException("Publisher key files already exist.");
    using var publisher = RSA.Create(3072);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    var privateOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
    if (!OperatingSystem.IsWindows()) privateOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    await using (var stream = new FileStream(args[1], privateOptions))
    await using (var writer = new StreamWriter(stream)) await writer.WriteAsync(publisher.ExportPkcs8PrivateKeyPem());
    await using (var stream = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None))
    await using (var writer = new StreamWriter(stream)) await writer.WriteAsync(publisher.ExportSubjectPublicKeyInfoPem());
    return;
}
if (args.Length == 3 && args[0] == "--check-key-pair")
{
    var publicPem = await File.ReadAllTextAsync(args[2]);
    if (publicPem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidDataException("Deployment public key file contains private key material.");
    using var privateKey = RSA.Create();
    using var publicKey = RSA.Create();
    privateKey.ImportFromPem(await File.ReadAllTextAsync(args[1]));
    publicKey.ImportFromPem(publicPem);
    var challenge = RandomNumberGenerator.GetBytes(32);
    if (!publicKey.VerifyData(challenge, privateKey.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        throw new InvalidDataException("Publisher key pair does not match.");
    return;
}
if (args.Length != 4)
    throw new ArgumentException("Usage: IntegrationPackager <publish-directory> <manifest-template.json> <publisher-private-key.pem> <output.zip>");
var input = Path.GetFullPath(args[0]);
var privatePath = Path.GetFullPath(args[2]);
if (privatePath.StartsWith(input.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    throw new InvalidDataException("Publisher private key must remain outside the published payload.");
var template = JsonSerializer.Deserialize<IntegrationPackageManifest>(await File.ReadAllTextAsync(args[1]), IntegrationJson.Options)
    ?? throw new InvalidDataException("Package template is invalid.");
var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (var path in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    var relative = Path.GetRelativePath(input, path).Replace('\\', '/');
    if (relative is "manifest.json" or "manifest.signature") throw new InvalidDataException("Published payload contains reserved manifest files.");
    await using var content = File.OpenRead(path);
    files[relative] = Convert.ToHexString(await SHA256.HashDataAsync(content));
}
if (!files.ContainsKey(template.EntryPoint)) throw new InvalidDataException("Package entry point is missing.");
var manifest = template with { Files = files };
var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, IntegrationJson.Options);
using var rsa = RSA.Create();
rsa.ImportFromPem(await File.ReadAllTextAsync(args[2]));
var signature = Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
var outputPath = Path.GetFullPath(args[3]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
{
    foreach (var relative in files.Keys)
        archive.CreateEntryFromFile(Path.Combine(input, relative), relative, CompressionLevel.Optimal);
    await using (var output = archive.CreateEntry("manifest.json").Open()) await output.WriteAsync(bytes);
    await using (var output = archive.CreateEntry("manifest.signature").Open()) await output.WriteAsync(Encoding.UTF8.GetBytes(signature));
}
await using var package = File.OpenRead(outputPath);
Console.WriteLine(Convert.ToHexString(await SHA256.HashDataAsync(package)));
