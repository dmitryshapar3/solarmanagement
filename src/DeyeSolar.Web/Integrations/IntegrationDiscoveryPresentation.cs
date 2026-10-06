using System.Text.Json;
using DeyeSolar.Domain.Billing;

namespace DeyeSolar.Web.Integrations;

public static class IntegrationDiscoveryPresentation
{
    public static bool? CanSwitch(IntegrationDiscoveryDevice device) => device.Metadata is { ValueKind: JsonValueKind.Object } metadata
        && metadata.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object
        && capabilities.TryGetProperty("canSwitch", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : null;

    public static bool AlreadySelected(IntegrationDiscoveryDevice device, IReadOnlyList<IntegrationBindingDto> selected)
        => selected.Any(binding => binding.Kind == device.Kind && binding.RemoteId == device.RemoteId && binding.Channel == device.Channel);

    public static bool RequiresPremium(BillingAccess? access, IntegrationDiscoveryDevice device, IReadOnlyList<IntegrationBindingDto> selected)
        => device.Kind == "socket" && !AlreadySelected(device, selected) && access?.SocketLimit is {} limit && access.SocketUsage >= limit;
}
