using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Billing;
using Microsoft.EntityFrameworkCore;
using SolarManagement.Integrations.Contracts;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Integrations;

/// <summary>Compatibility facade; inventory/cache and durable command lifecycle have separate owners.</summary>
public sealed class DynamicSocketGateway : ISmartSocketCatalog, ISocketCommandTracker, ISocketController, ISocketInventoryService,
    IAccountSocketCatalog, IAccountSocketControl
{
    private readonly SocketCommandLifecycle _commands;
    private readonly SocketInventoryReader _inventory;
    public DynamicSocketGateway(IIntegrationRegistry registry, IIntegrationRuntimeExecutor executor,
        IDbContextFactory<DeyeSolarDbContext> factory, TimeProvider clock, IBillingAccessReader? billing = null)
    {
        ISocketAccessPolicy access = billing is null ? UnrestrictedSocketAccessForTests.Instance : new BillingSocketAccessPolicy(factory, billing);
        var bindings = new SocketBindingSessionGuard(registry);
        var observations = new SocketObservationReader(executor, bindings, access, clock);
        _commands = new(registry, executor, factory, clock, access, observations, bindings);
        _inventory = new(registry, this, access, clock);
        _commands.CommandCompleted += _inventory.Invalidate;
    }
    public void Invalidate() => _inventory.Invalidate();
    public Task<ISmartSocket> GetAsync(SocketId id, CancellationToken ct) => _commands.GetAsync(id, ct);
    public Task<ISmartSocket> GetForUserAsync(SocketId id, string userId, CancellationToken ct) => _commands.GetForUserAsync(id, userId, ct);
    public Task<ISmartSocket> GetForUserAsync(SocketId id, string userId, Func<CancellationToken, Task> authorizeCommand, CancellationToken ct)
        => _commands.GetForUserAsync(id, userId, authorizeCommand, ct);
    public Task<SocketInventorySnapshot> ReadInventoryAsync(bool forceRefresh, CancellationToken ct) => _inventory.ReadInventoryAsync(forceRefresh, ct);
    public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct) => _inventory.GetCachedDevicesAsync(ct);
    public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct) => _inventory.RefreshDevicesAsync(ct);
    public Task TurnOnAsync(string entityId, CancellationToken ct) => _commands.TurnOnAsync(entityId, ct);
    public Task TurnOffAsync(string entityId, CancellationToken ct) => _commands.TurnOffAsync(entityId, ct);
    public Task SetPowerForUserAsync(string entityId, SwitchState desiredState, string userId, CancellationToken ct) => _commands.SetPowerForUserAsync(entityId, desiredState, userId, ct);
    public Task SetPowerForUserAsync(string entityId, SwitchState desiredState, string userId, Func<CancellationToken, Task> authorizeCommand, CancellationToken ct)
        => _commands.SetPowerForUserAsync(entityId, desiredState, userId, authorizeCommand, ct);
    public Task<bool> GetStateAsync(string entityId, CancellationToken ct) => _commands.GetStateAsync(entityId, ct);
    public Task<SocketCommandResult> ReadResultAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct) => _commands.ReadResultAsync(deviceId, commandId, ct);
    public Task<IReadOnlyList<SocketCommandReceipt>> ListUnresolvedAsync(SocketId deviceId, CancellationToken ct) => _commands.ListUnresolvedAsync(deviceId, ct);
    public Task<IReadOnlyList<IntegrationCommandReceipt>> UnresolvedAsync(Guid deviceId, CancellationToken ct) => _commands.UnresolvedAsync(deviceId, ct);
    public Task<IntegrationCommandReceipt> DescribeResultAsync(Guid deviceId, Guid commandId, CancellationToken ct) => _commands.DescribeResultAsync(deviceId, commandId, ct);
    public Task<IntegrationCommandReceipt> ReleaseAsync(Guid deviceId, Guid commandId, CancellationToken ct) => _commands.ReleaseAsync(deviceId, commandId, ct);
    public Task<SocketCommandReceipt> ReleaseAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct) => _commands.ReleaseAsync(deviceId, commandId, ct);
    public Task<SocketCommandReceipt> ReleaseForUserAsync(SocketId deviceId, SocketCommandId commandId, string userId,
        Func<CancellationToken, Task> authorizeCommand, CancellationToken ct)
        => _commands.ReleaseForUserAsync(deviceId, commandId, userId, authorizeCommand, ct);
}
