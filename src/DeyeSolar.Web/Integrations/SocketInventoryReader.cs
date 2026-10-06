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

/// <summary>Observes inventory and caches only completed observations from the current integration epoch.</summary>
internal sealed class SocketInventoryReader(IIntegrationRegistry registry, ISmartSocketCatalog sockets, ISocketAccessPolicy access, TimeProvider clock)
{
    private readonly object _inventorySync = new();
    private InventoryCache? _inventory;
    private long _inventoryEpoch;
    private long _inventoryRequest;
    public void Invalidate() { lock (_inventorySync) { _inventoryEpoch++; _inventory = null; } }
    public async Task<SocketInventorySnapshot> ReadInventoryAsync(bool forceRefresh, CancellationToken ct)
        => (await ReadCacheAsync(forceRefresh, ct)).Snapshot;
    private async Task<InventoryCache> ReadCacheAsync(bool forceRefresh, CancellationToken ct)
    {
        await access.EnsureAsync(ct);
        long epoch;
        long request;
        lock (_inventorySync)
        {
            if (!forceRefresh && _inventory is { } cached && clock.GetUtcNow() - cached.Snapshot.RefreshedAt < TimeSpan.FromSeconds(30)) return cached;
            epoch = _inventoryEpoch;
            request = ++_inventoryRequest;
        }
        var bindings = (await registry.ListBindingsAsync(ct)).Where(b => b.Kind == "socket").ToArray();
        var devices = new List<SocketDescriptor>();
        var displayDevices = new List<DevicePowerInfo>();
        var issues = new List<SocketInventoryIssue>();
        foreach (var binding in bindings)
        {
            var capabilities = SocketCommandLifecycle.Capabilities(binding);
            var displayName = IntegrationDeviceDisplayName.Read(binding) ?? binding.Name;
            try
            {
                var socket = await sockets.GetAsync(new(binding.Id), ct);
                var state = await socket.ReadAsync(ct);
                devices.Add(new(new(binding.Id), displayName, capabilities, state.Reachability));
                displayDevices.Add(new(binding.Id.ToString("D"), displayName, "Socket", state.Reachability == Reachability.Online,
                    state.Power == SwitchState.On, state.CurrentPower?.Value, state.Reachability == Reachability.Online && state.Power != SwitchState.Unknown
                    && (state.ObservedAt is null || clock.GetUtcNow() - state.ObservedAt <= TimeSpan.FromMinutes(10))) { CloudName = binding.Name });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (BillingAccessException) { throw; }
            catch
            {
                devices.Add(new(new(binding.Id), displayName, capabilities, Reachability.Unknown));
                issues.Add(new(displayName, SocketInventoryIssueKind.Unavailable));
                displayDevices.Add(new(binding.Id.ToString("D"), displayName, "Socket", false, false, null, false) { CloudName = binding.Name });
            }
        }
        await access.EnsureAsync(ct);
        lock (_inventorySync)
        {
            if (epoch != _inventoryEpoch) throw new InvalidOperationException("Socket integrations changed during discovery.");
            // Pending or cancelled readers cannot suppress a successful refresh; completed newer results win.
            if (_inventory is { } current && current.Request > request) return current;
            return _inventory = new(request, new(devices, issues, clock.GetUtcNow()), displayDevices);
        }
    }
    public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct) => InventoryAsync(false, ct);
    public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct) => InventoryAsync(true, ct);
    private async Task<IReadOnlyList<DevicePowerInfo>> InventoryAsync(bool force, CancellationToken ct)
    {
        var inventory = await ReadCacheAsync(force, ct);
        var result = inventory.DisplayDevices;
        if (inventory.Snapshot.Issues.Count > 0 && result.All(d => !d.Online))
            throw new InvalidOperationException("Socket integrations are unavailable. Previously registered devices have been retained.");
        return result;
    }
    private sealed record InventoryCache(long Request, SocketInventorySnapshot Snapshot, IReadOnlyList<DevicePowerInfo> DisplayDevices);
}
