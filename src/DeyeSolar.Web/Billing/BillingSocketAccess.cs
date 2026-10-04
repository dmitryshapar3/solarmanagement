using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Billing;

// Shared installation automation uses the gateway directly. Interactive callers also need their own account's access.
public sealed class BillingSocketAccess(ISmartSocketCatalog inventory, ISocketInventoryService devices,
    ISocketController states, ISocketCommandTracker commands, IAccountSocketCatalog accounts,
    IAccountSocketControl control, IBillingAccessReader billing, CurrentBillingAccount current,
    InteractiveSecurityContext security)
    : ISmartSocketCatalog, ISocketController, ISocketInventoryService, ISocketCommandTracker
{
    private async Task EnsureAsync(CancellationToken ct, InstallationPermission permission = InstallationPermission.Read)
    {
        await security.EnsureAsync(permission, ct);
        if (current.UserId is not { } userId || !(await billing.ReadAsync(userId, ct)).HasAccess)
            throw new BillingAccessException();
    }

    private async Task AuthorizeCommandAsync(CancellationToken ct)
        => await EnsureAsync(ct, InstallationPermission.ControlDevices);

    public async Task<ISmartSocket> GetAsync(SocketId id, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var socket = await accounts.GetForUserAsync(id, current.UserId!, AuthorizeCommandAsync, ct);
        await EnsureAsync(ct);
        return new AccountSocket(this, socket);
    }

    public async Task<SocketInventorySnapshot> ReadInventoryAsync(bool forceRefresh, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await inventory.ReadInventoryAsync(forceRefresh, ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await devices.GetCachedDevicesAsync(ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await devices.RefreshDevicesAsync(ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<bool> GetStateAsync(string entityId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await states.GetStateAsync(entityId, ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task TurnOnAsync(string entityId, CancellationToken ct)
    {
        await EnsureAsync(ct, InstallationPermission.ControlDevices);
        await control.SetPowerForUserAsync(entityId, SwitchState.On, current.UserId!, AuthorizeCommandAsync, ct);
    }

    public async Task TurnOffAsync(string entityId, CancellationToken ct)
    {
        await EnsureAsync(ct, InstallationPermission.ControlDevices);
        await control.SetPowerForUserAsync(entityId, SwitchState.Off, current.UserId!, AuthorizeCommandAsync, ct);
    }

    public async Task<SocketCommandResult> ReadResultAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await commands.ReadResultAsync(deviceId, commandId, ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<SocketCommandReceipt>> ListUnresolvedAsync(SocketId deviceId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await commands.ListUnresolvedAsync(deviceId, ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<SocketCommandReceipt> ReleaseAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct)
    {
        await EnsureAsync(ct, InstallationPermission.ControlDevices);
        return await control.ReleaseForUserAsync(deviceId, commandId, current.UserId!, AuthorizeCommandAsync, ct);
    }

    private sealed class AccountSocket(BillingSocketAccess owner, ISmartSocket inner) : ISmartSocket
    {
        public SocketId Id => inner.Id;
        public SocketCapabilities Capabilities => inner.Capabilities;

        public async Task<SocketState> ReadAsync(CancellationToken ct)
        {
            await owner.EnsureAsync(ct);
            var result = await inner.ReadAsync(ct);
            await owner.EnsureAsync(ct);
            return result;
        }

        public async Task<SocketCommandResult> SetPowerAsync(SetSocketPowerCommand command, CancellationToken ct)
        {
            await owner.EnsureAsync(ct, InstallationPermission.ControlDevices);
            return await inner.SetPowerAsync(command, ct);
        }
    }
}

public static class BillingSocketAccessRegistration
{
    public static IServiceCollection AddBillingSocketAccess(this IServiceCollection services)
    {
        services.AddScoped<CurrentBillingAccount>();
        services.AddScoped(provider =>
        {
            var gateway = provider.GetRequiredService<DynamicSocketGateway>();
            return new BillingSocketAccess(gateway, gateway, gateway, gateway, gateway, gateway,
                provider.GetRequiredService<IBillingAccessReader>(), provider.GetRequiredService<CurrentBillingAccount>(),
                provider.GetRequiredService<InteractiveSecurityContext>());
        });
        services.RemoveAll<ISmartSocketCatalog>();
        services.RemoveAll<ISocketController>();
        services.RemoveAll<ISocketInventoryService>();
        services.RemoveAll<ISocketCommandTracker>();
        services.AddScoped<ISmartSocketCatalog>(provider => provider.GetRequiredService<BillingSocketAccess>());
        services.AddScoped<ISocketController>(provider => provider.GetRequiredService<BillingSocketAccess>());
        services.AddScoped<ISocketInventoryService>(provider => provider.GetRequiredService<BillingSocketAccess>());
        services.AddScoped<ISocketCommandTracker>(provider => provider.GetRequiredService<BillingSocketAccess>());
        return services;
    }
}
