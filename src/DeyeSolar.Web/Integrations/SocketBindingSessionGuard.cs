using DeyeSolar.Web.Data;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Integrations;

internal sealed class SocketBindingSessionGuard(IIntegrationRegistry registry)
{
    public async Task<IntegrationSession> CurrentAsync(IntegrationDeviceBindingEntity binding,
        IntegrationSession expected, CancellationToken ct)
    {
        if (await registry.FindBindingAsync(binding.Id, ct) is not { Enabled: true, Kind: "socket" })
            throw new InvalidOperationException("The socket binding is disabled.");
        var current = await registry.GetRuntimeSessionAsync(binding.InstanceId, ct);
        if (current.ConfigurationRevision != expected.ConfigurationRevision || current.Generation != expected.Generation)
            throw new InvalidOperationException("The socket connection changed. Refresh and try again.");
        return current;
    }
}
