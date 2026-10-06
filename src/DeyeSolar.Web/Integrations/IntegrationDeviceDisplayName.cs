using System.Text.Json;
using System.Text.Json.Nodes;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Integrations;

/// <summary>A host-owned display label; it never changes provider identity or capability data.</summary>
public static class IntegrationDeviceDisplayName
{
    private const string Key = "smartSolarDisplayName";
    public static string? Read(IntegrationDeviceBindingEntity binding)
    {
        try
        {
            using var metadata = JsonDocument.Parse(binding.MetadataJson);
            if (metadata.RootElement.ValueKind == JsonValueKind.Object
                && metadata.RootElement.TryGetProperty(Key, out var value) && value.ValueKind == JsonValueKind.String
                && DeviceNameService.TryName(value.GetString(), out var name)) return name;
        }
        catch (JsonException) { }
        return null;
    }
    public static string Write(IntegrationDeviceBindingEntity binding, string? name)
    {
        if (!DeviceNameService.TryName(name, out var normalized))
            throw new ArgumentException("Use a device name of up to 80 characters without control characters.");
        JsonObject metadata;
        try { metadata = JsonNode.Parse(binding.MetadataJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { metadata = new JsonObject(); }
        if (normalized is null) metadata.Remove(Key); else metadata[Key] = normalized;
        return metadata.ToJsonString();
    }
}
