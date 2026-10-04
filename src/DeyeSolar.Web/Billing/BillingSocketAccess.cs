using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Integrations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Billing;

// Shared installation automation uses the gateway directly. Interactive callers also need their own account's access.
public sealed class BillingSocketAccess(DynamicSocketGateway inner, BillingAccessService billing, CurrentBillingAccount current)
    : ISmartSocketCatalog, ISocketController, ISocketInventoryService, ISocketCommandTracker
{
    private async Task EnsureAsync(CancellationToken ct)
    {
        if (current.UserId is not { } userId || !(await billing.ReadAsync(userId, ct)).HasAccess)
            throw new BillingAccessException();
    }

    public async Task<ISmartSocket> GetAsync(SocketId id, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var socket = await inner.GetForUserAsync(id, current.UserId!, ct);
        await EnsureAsync(ct);
        return new AccountSocket(this, socket);
    }

    public async Task<SocketInventorySnapshot> ReadInventoryAsync(bool forceRefresh, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await inner.ReadInventoryAsync(forceRefresh, ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await inner.GetCachedDevicesAsync(ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await inner.RefreshDevicesAsync(ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<bool> GetStateAsync(string entityId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await inner.GetStateAsync(entityId, ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task TurnOnAsync(string entityId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        await inner.SetPowerForUserAsync(entityId, SwitchState.On, current.UserId!, ct);
    }

    public async Task TurnOffAsync(string entityId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        await inner.SetPowerForUserAsync(entityId, SwitchState.Off, current.UserId!, ct);
    }

    public async Task<SocketCommandResult> ReadResultAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await inner.ReadResultAsync(deviceId, commandId, ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<SocketCommandReceipt>> ListUnresolvedAsync(SocketId deviceId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var result = await ((ISocketCommandTracker)inner).ListUnresolvedAsync(deviceId, ct);
        await EnsureAsync(ct);
        return result;
    }

    public async Task<SocketCommandReceipt> ReleaseAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await ((ISocketCommandTracker)inner).ReleaseAsync(deviceId, commandId, ct);
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
            await owner.EnsureAsync(ct);
            return await inner.SetPowerAsync(command, ct);
        }
    }
}

public static class BillingSocketAccessRegistration
{
    public static IServiceCollection AddBillingSocketAccess(this IServiceCollection services)
    {
        services.AddScoped<CurrentBillingAccount>();
        services.AddScoped<BillingSocketAccess>();
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
