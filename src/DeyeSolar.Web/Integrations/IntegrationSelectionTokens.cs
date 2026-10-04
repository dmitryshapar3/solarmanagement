using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Billing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.Runtime;

namespace DeyeSolar.Web.Integrations;

public sealed record IntegrationSelectionProof(string InstallationId, Guid InstanceId, long Revision, string PackageVersion,
    string PackageDigest, string DescriptorDigest, string Fingerprint, DateTimeOffset ExpiresAt, IntegrationDiscoveredDevice Device);

public interface IIntegrationSelectionTokens
{
    IntegrationDiscoveryResponse Issue(IntegrationInstanceEntity instance, IReadOnlyList<IntegrationDiscoveredDevice> devices,
        string fingerprint, DateTimeOffset expiry);
    IntegrationSelectionProof Read(string token);
}

/// <summary>Authenticates discovered device selections and limits their public capability metadata.</summary>
public sealed class IntegrationSelectionTokens(IDataProtectionProvider protection) : IIntegrationSelectionTokens
{
    private IDataProtector Protector => protection.CreateProtector("IntegrationDiscoverySelection.v1");
    public IntegrationDiscoveryResponse Issue(IntegrationInstanceEntity instance, IReadOnlyList<IntegrationDiscoveredDevice> devices,
        string fingerprint, DateTimeOffset expiry)
        => new(devices.Select(PublicDevice).Select(device => new IntegrationDiscoveryDevice(Protector.Protect(JsonSerializer.Serialize(
            new IntegrationSelectionProof(instance.InstallationId, instance.Id, instance.Revision, instance.PackageVersion, instance.PackageDigest,
                instance.DescriptorDigest, fingerprint, expiry, device), IntegrationJson.Options)), device.Name, device.Kind,
                device.RemoteId, device.Channel ?? "", device.Metadata)).ToList(), expiry);
    public IntegrationSelectionProof Read(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 32768) throw new InvalidDataException();
            return JsonSerializer.Deserialize<IntegrationSelectionProof>(Protector.Unprotect(token), IntegrationJson.Options) ?? throw new InvalidDataException();
        }
        catch (Exception error) when (error is CryptographicException or JsonException or InvalidDataException)
        { throw new IntegrationRequestException("invalid_selection", "Discover devices again before selecting this device."); }
    }
    private static IntegrationDiscoveredDevice PublicDevice(IntegrationDiscoveredDevice device)
    {
        var capabilities = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (device.Metadata is { ValueKind: JsonValueKind.Object } metadata
            && metadata.TryGetProperty("capabilities", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "hasBattery", "hasSolarPower", "hasSignedGridPower", "hasLoadPower", "hasGridPowerHistory", "canSwitch", "canMeasurePower" })
                if (source.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    capabilities[key] = value.Clone();
            if (source.TryGetProperty("solarBasis", out var basis) && basis.ValueKind == JsonValueKind.String
                && basis.GetString() is "Unknown" or "PvDc" or "InverterAcOutput") capabilities["solarBasis"] = basis.Clone();
        }
        // Workers return only public capability data across the discovery boundary.
        return device with { Metadata = IntegrationJson.Element(new { capabilities }) };
    }
}
