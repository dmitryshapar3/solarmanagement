using System.Security.Claims;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Integrations;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Redesign;

public sealed class ManualOverrideService(DynamicSocketGateway gateway, InteractiveSecurityContext security)
{
    public async Task<IntegrationCommandReceipt> SwitchAsync(ClaimsPrincipal actor, Guid deviceId, Guid commandId,
        bool isOn, string? onRuleConflict, CancellationToken ct, string client = "app")
    {
        if (onRuleConflict is not (null or "pause" or "once"))
            throw new ArgumentException("Choose Pause automation or Just this once.");
        var userId = actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
        security.BindOnce(actor);
        async Task Authorize(CancellationToken token)
        {
            await security.EnsureAsync(InstallationPermission.ControlDevices, token);
            if (onRuleConflict == "pause") await security.EnsureAsync(InstallationPermission.ManageRules, token);
        }
        await Authorize(ct);
        if (client is not ("app" or "web")) throw new ArgumentException("Invalid command client.");
        using var choice = ManualOverrideContext.Enter(onRuleConflict, userId, client);
        var socket = await gateway.GetForUserAsync(new(deviceId), userId, Authorize, ct);
        await socket.SetPowerAsync(new(new(commandId), isOn ? SwitchState.On : SwitchState.Off), ct);
        return await gateway.DescribeResultAsync(deviceId, commandId, ct);
    }
}

internal static class ManualOverrideContext
{
    internal sealed record Choice(string? Policy, string UserId, string Client);
    private static readonly AsyncLocal<Choice?> Slot = new();
    public static Choice? Current => Slot.Value;
    public static IDisposable Enter(string? policy, string userId, string client)
    {
        var previous = Slot.Value;
        Slot.Value = new(policy, userId, client);
        return new Scope(previous);
    }
    private sealed class Scope(Choice? previous) : IDisposable { public void Dispose() => Slot.Value = previous; }
}
