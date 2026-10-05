using System.Text.Json;
using SolarManagement.Inverters.Contracts;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Integrations;

internal sealed record IntegrationDeviceCapabilities(InverterCapabilities Inverter, SocketCapabilities Socket);

internal static class IntegrationCapabilities
{
    public static InverterCapabilities Read(IntegrationDeviceBindingEntity binding) => ReadMetadata(binding.MetadataJson).Inverter;
    public static SocketCapabilities ReadSocket(IntegrationDeviceBindingEntity binding) => ReadMetadata(binding.MetadataJson).Socket;
    public static IntegrationDeviceCapabilities ReadMetadata(string? metadata)
    {
        var unknown = new IntegrationDeviceCapabilities(new(false, false, false, false, false, SolarPowerBasis.Unknown), new(false, false));
        if (metadata is null) return unknown;
        try
        {
            using var json = JsonDocument.Parse(metadata);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("capabilities", out var value)
                || value.ValueKind != JsonValueKind.Object) return unknown;
            bool Flag(string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
            var basis = value.TryGetProperty("solarBasis", out var property) && property.ValueKind == JsonValueKind.String
                && Enum.TryParse<SolarPowerBasis>(property.GetString(), out var parsed) && Enum.IsDefined(parsed)
                ? parsed : SolarPowerBasis.Unknown;
            return new(new(Flag("hasBattery"), Flag("hasSolarPower"), Flag("hasSignedGridPower"), Flag("hasLoadPower"), Flag("hasGridPowerHistory"), basis),
                new(Flag("canSwitch"), Flag("canMeasurePower")));
        }
        catch (JsonException) { return unknown; }
    }
}
