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

public sealed class DynamicSocketGateway(IIntegrationRegistry registry, IIntegrationRuntimeExecutor executor,
    IDbContextFactory<DeyeSolarDbContext> factory, TimeProvider clock, BillingAccessService? billing = null) : ISmartSocketCatalog,
    ISocketCommandTracker, ISocketController, ISocketInventoryService
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _commands = new();
    private readonly object _inventorySync = new();
    private InventoryCache? _inventory;
    private long _inventoryEpoch;
    private long _inventoryRequest;
    public void Invalidate() { lock (_inventorySync) { _inventoryEpoch++; _inventory = null; } }
    public Task<ISmartSocket> GetAsync(SocketId id, CancellationToken ct) => GetSocketAsync(id, null, ct);

    public Task<ISmartSocket> GetForUserAsync(SocketId id, string userId, CancellationToken ct)
        => GetSocketAsync(id, userId, ct);

    private async Task<ISmartSocket> GetSocketAsync(SocketId id, string? userId, CancellationToken ct)
    {
        await EnsureAccessAsync(ct, userId);
        var binding = await registry.FindBindingAsync(id.Value, ct);
        if (binding is not { Enabled: true, Kind: "socket" })
            throw new InvalidOperationException("Choose an available socket from this installation.");
        var session = await registry.GetRuntimeSessionAsync(binding.InstanceId, ct);
        return new Socket(this, binding, session, userId);
    }
    public async Task<SocketInventorySnapshot> ReadInventoryAsync(bool forceRefresh, CancellationToken ct)
        => (await ReadCacheAsync(forceRefresh, ct)).Snapshot;
    private async Task<InventoryCache> ReadCacheAsync(bool forceRefresh, CancellationToken ct)
    {
        await EnsureAccessAsync(ct);
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
        var legacy = new List<DevicePowerInfo>();
        var issues = new List<SocketInventoryIssue>();
        foreach (var binding in bindings)
        {
            var capabilities = Capabilities(binding);
            try
            {
                var socket = await GetAsync(new(binding.Id), ct);
                var state = await socket.ReadAsync(ct);
                devices.Add(new(new(binding.Id), binding.Name, capabilities, state.Reachability));
                legacy.Add(new(binding.Id.ToString("D"), binding.Name, "Socket", state.Reachability == Reachability.Online,
                    state.Power == SwitchState.On, state.CurrentPower?.Value));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (BillingAccessException) { throw; }
            catch
            {
                devices.Add(new(new(binding.Id), binding.Name, capabilities, Reachability.Unknown));
                issues.Add(new(binding.Name, SocketInventoryIssueKind.Unavailable));
                legacy.Add(new(binding.Id.ToString("D"), binding.Name, "Socket", false, false, null));
            }
        }
        await EnsureAccessAsync(ct);
        lock (_inventorySync)
        {
            if (epoch != _inventoryEpoch) throw new InvalidOperationException("Socket integrations changed during discovery.");
            // Pending or cancelled readers cannot suppress a successful refresh; completed newer results win.
            if (_inventory is { } current && current.Request > request) return current;
            return _inventory = new(request, new(devices, issues, clock.GetUtcNow()), legacy);
        }
    }
    public Task<IReadOnlyList<DevicePowerInfo>> GetCachedDevicesAsync(CancellationToken ct) => LegacyInventoryAsync(false, ct);
    public Task<IReadOnlyList<DevicePowerInfo>> RefreshDevicesAsync(CancellationToken ct) => LegacyInventoryAsync(true, ct);
    private async Task<IReadOnlyList<DevicePowerInfo>> LegacyInventoryAsync(bool force, CancellationToken ct)
    {
        var inventory = await ReadCacheAsync(force, ct);
        var result = inventory.Legacy;
        if (inventory.Snapshot.Issues.Count > 0 && result.All(d => !d.Online))
            throw new InvalidOperationException("Socket integrations are unavailable. Previously registered devices have been retained.");
        return result;
    }
    public Task TurnOnAsync(string entityId, CancellationToken ct) => LegacyCommandAsync(entityId, true, ct);
    public Task TurnOffAsync(string entityId, CancellationToken ct) => LegacyCommandAsync(entityId, false, ct);
    public Task SetPowerForUserAsync(string entityId, SwitchState desiredState, string userId, CancellationToken ct)
        => desiredState is SwitchState.On or SwitchState.Off
            ? LegacyCommandAsync(entityId, desiredState == SwitchState.On, ct, userId)
            : throw new ArgumentException("Provide an explicit On or Off state.", nameof(desiredState));

    private async Task LegacyCommandAsync(string entityId, bool desired, CancellationToken ct, string? userId = null)
    {
        var id = await ResolveIdAsync(entityId, ct);
        var socket = await GetSocketAsync(new(id), userId, ct);
        var result = await socket.SetPowerAsync(new(new(Guid.NewGuid()), desired ? SwitchState.On : SwitchState.Off), ct);
        if (result.Status != SocketCommandStatus.Acknowledged)
            throw new InvalidOperationException($"The socket command is {result.Status.ToString().ToLowerInvariant()}; it has not been confirmed.");
    }
    public async Task<bool> GetStateAsync(string entityId, CancellationToken ct)
    {
        var state = await (await GetAsync(new(await ResolveIdAsync(entityId, ct)), ct)).ReadAsync(ct);
        return state.Power switch
        {
            SwitchState.On => true,
            SwitchState.Off => false,
            _ => throw new InvalidOperationException("The socket state is unknown.")
        };
    }
    private async Task<Guid> ResolveIdAsync(string entityId, CancellationToken ct)
    {
        if (Guid.TryParse(entityId, out var id)) return id;
        await using var db = await factory.CreateDbContextAsync(ct);
        var alias = await db.IntegrationDeviceAliases.AsNoTracking().SingleOrDefaultAsync(a => a.LegacyId == entityId, ct);
        return alias?.DeviceId ?? throw new InvalidOperationException("The device identity is unknown. Refresh devices.");
    }
    public async Task<SocketCommandResult> ReadResultAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct)
    {
        await EnsureAccessAsync(ct);
        var binding = await registry.FindBindingAsync(deviceId.Value, ct)
            ?? throw new InvalidOperationException("The socket is unknown in this installation.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var command = await db.IntegrationCommands.SingleOrDefaultAsync(c => c.Id == commandId.Value && c.DeviceId == deviceId.Value, ct)
            ?? throw new InvalidOperationException("The command is unknown for this device.");
        if (command.Status is "pending" or "requested" or "uncertain")
        {
            var snapshot = await registry.GetSnapshotAsync(binding.InstanceId, ct);
            if (!binding.Enabled || snapshot?.Instance is not { Status: "enabled" } current
                || current.Revision != command.Revision || current.Generation != command.Generation)
            {
                // A retired session cannot authenticate a result through its replacement credentials.
                if (command.Status != "uncertain" || command.ErrorCode != "retired_generation")
                {
                    command.Status = "uncertain";
                    command.ErrorCode = "retired_generation";
                    command.CompletedAt = clock.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                }
                return Receipt(command);
            }
            if (command.Status == "requested")
            {
                // A process restart may have interrupted the send. There is no evidence that it is safe to replay.
                if (clock.GetUtcNow() - command.CreatedAt < TimeSpan.FromMinutes(2)) return Receipt(command);
                command.Status = "uncertain";
                command.ErrorCode = "interrupted_send";
                await db.SaveChangesAsync(ct);
            }
            if (command.ProviderOperationId is not null)
            {
                try
                {
                    var session = await registry.GetRuntimeSessionAsync(binding.InstanceId, ct);
                    if (session.ConfigurationRevision != command.Revision || session.Generation != command.Generation)
                    {
                        command.Status = "uncertain";
                        command.ErrorCode = "retired_generation";
                        command.CompletedAt = clock.GetUtcNow();
                        await db.SaveChangesAsync(ct);
                        return Receipt(command);
                    }
                    var json = await executor.InvokeAsync(session, "socket.result", IntegrationJson.Element(new
                    {
                        remoteId = binding.RemoteId,
                        channel = binding.Channel,
                        commandId = command.Id.ToString("D"),
                        operationToken = command.ProviderOperationId
                    }), ct);
                    var response = json.Deserialize<ProviderSocketCommandResult>(IntegrationJson.Options)
                        ?? throw new InvalidDataException("Missing command receipt.");
                    ApplyResponse(command, response);
                    await CompleteAsync(db, binding, session, command);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    // An older result must return the durable winner after a concurrent close or acknowledgement.
                    await db.Entry(command).ReloadAsync(ct);
                }
            }
            // A repeated Pending response may produce no UPDATE and therefore no concurrency exception.
            await db.Entry(command).ReloadAsync(ct);
        }
        return Receipt(command);
    }
    public async Task<IReadOnlyList<IntegrationCommandReceipt>> UnresolvedAsync(Guid deviceId, CancellationToken ct)
    {
        await EnsureAccessAsync(ct);
        if (await registry.FindBindingAsync(deviceId, ct) is not { Kind: "socket" })
            throw new InvalidOperationException("The socket is unknown in this installation.");
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.IntegrationCommands.AsNoTracking().Where(c => c.DeviceId == deviceId
            && (c.Status == "requested" || c.Status == "pending" || c.Status == "uncertain"))
            .OrderBy(c => c.CreatedAt).Take(100).ToListAsync(ct)).Select(ToDto).ToArray();
    }
    public async Task<IntegrationCommandReceipt> DescribeResultAsync(Guid deviceId, Guid commandId, CancellationToken ct)
    {
        await ReadResultAsync(new(deviceId), new(commandId), ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        return ToDto(await db.IntegrationCommands.AsNoTracking().SingleAsync(c => c.Id == commandId && c.DeviceId == deviceId, ct));
    }
    public async Task<IntegrationCommandReceipt> ReleaseAsync(Guid deviceId, Guid commandId, CancellationToken ct)
    {
        await ReadResultAsync(new(deviceId), new(commandId), ct);
        var socket = await GetAsync(new(deviceId), ct);
        var state = await socket.ReadAsync(ct);
        if (state.Power == SwitchState.Unknown || state.Reachability != Reachability.Online)
            throw new InvalidOperationException("Read the online socket's current state before allowing another command.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var command = await db.IntegrationCommands.SingleOrDefaultAsync(c => c.Id == commandId && c.DeviceId == deviceId, ct)
            ?? throw new InvalidOperationException("The command is unknown for this device.");
        if (command.Status is "pending" or "requested")
            throw new InvalidOperationException("This command is still pending. Wait for an authoritative terminal result before sending another command.");
        if (command.Status != "uncertain") return ToDto(command);
        command.Status = "uncertain_closed";
        command.ErrorCode = "released_after_observation";
        command.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        try { await executor.ReleaseCommandTrackingAsync(command.InstanceId, command.Id, CancellationToken.None); }
        catch (ObjectDisposedException) { /* Shutdown already discarded process-local tracking; the durable release remains complete. */ }
        return ToDto(command);
    }
    private static IntegrationCommandReceipt ToDto(IntegrationCommandEntity c)
        => new(c.Id, c.DeviceId, c.DesiredState, c.Status == "uncertain_closed" ? c.Status : Receipt(c).Status.ToString().ToLowerInvariant(), c.ErrorCode, c.CreatedAt, c.CompletedAt);
    public async Task<IReadOnlyList<SocketCommandReceipt>> ListUnresolvedAsync(SocketId deviceId, CancellationToken ct)
        => (await UnresolvedAsync(deviceId.Value, ct)).Select(ToContract).ToArray();
    public async Task<SocketCommandReceipt> ReleaseAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct)
        => ToContract(await ReleaseAsync(deviceId.Value, commandId.Value, ct));
    private static SocketCommandReceipt ToContract(IntegrationCommandReceipt receipt)
        => new(new(receipt.DeviceId), new(receipt.CommandId), receipt.IsOn ? SwitchState.On : SwitchState.Off,
            receipt.Status == "uncertain_closed" ? SocketCommandStatus.UncertainClosed : Enum.Parse<SocketCommandStatus>(receipt.Status, true), receipt.CreatedAt, receipt.CompletedAt);
    private static SocketCommandResult Receipt(IntegrationCommandEntity command) => new(new(command.Id),
        command.Status switch
        {
            "acknowledged" => SocketCommandStatus.Acknowledged,
            "rejected" => SocketCommandStatus.Rejected,
            "requested" or "pending" => SocketCommandStatus.Pending,
            "uncertain_closed" => SocketCommandStatus.UncertainClosed,
            _ => SocketCommandStatus.Uncertain
        },
        command.Status == "rejected" ? command.ErrorCode switch
        {
            "unsupported" => SocketCommandRejection.Unsupported,
            "offline" => SocketCommandRejection.Offline,
            "configuration_changed" => SocketCommandRejection.ConfigurationChanged,
            _ => SocketCommandRejection.DeviceUnavailable
        } : null, null);
    private static SocketCapabilities Capabilities(IntegrationDeviceBindingEntity binding)
    {
        using var json = JsonDocument.Parse(binding.MetadataJson);
        if (!json.RootElement.TryGetProperty("capabilities", out var value)) return new(false, false);
        return new(value.TryGetProperty("canSwitch", out var s) && s.ValueKind == JsonValueKind.True,
            value.TryGetProperty("canMeasurePower", out var p) && p.ValueKind == JsonValueKind.True);
    }
    private async Task<IntegrationSession> CurrentAsync(IntegrationDeviceBindingEntity binding,
        IntegrationSession expected, CancellationToken ct)
    {
        if (await registry.FindBindingAsync(binding.Id, ct) is not { Enabled: true, Kind: "socket" })
            throw new InvalidOperationException("The socket binding is disabled.");
        var current = await registry.GetRuntimeSessionAsync(binding.InstanceId, ct);
        if (current.ConfigurationRevision != expected.ConfigurationRevision || current.Generation != expected.Generation)
            throw new InvalidOperationException("The socket connection changed. Refresh and try again.");
        return current;
    }
    private async Task<SocketState> ReadAsync(IntegrationDeviceBindingEntity binding, IntegrationSession expected, CancellationToken ct)
    {
        await EnsureAccessAsync(ct);
        var session = await CurrentAsync(binding, expected, ct);
        var json = await executor.InvokeAsync(session, "socket.read", IntegrationJson.Element(new { remoteId = binding.RemoteId, channel = binding.Channel }), ct);
        await CurrentAsync(binding, expected, ct);
        var data = json.Deserialize<ProviderSocketTelemetry>(IntegrationJson.Options) ?? throw new InvalidDataException("Missing socket state.");
        await EnsureAccessAsync(ct);
        if (data.RemoteId != binding.RemoteId || (data.Channel ?? "") != binding.Channel
            || data.CurrentPowerWatts < 0 || data.ObservedAt > clock.GetUtcNow())
            throw new InvalidDataException("The socket observation has invalid provenance or values.");
        return new(new(binding.Id), data.IsOn is null ? SwitchState.Unknown : data.IsOn.Value ? SwitchState.On : SwitchState.Off,
            data.Online is null ? Reachability.Unknown : data.Online.Value ? Reachability.Online : Reachability.Offline,
            data.CurrentPowerWatts is { } watts ? new Watts(watts) : null, data.ObservedAt, clock.GetUtcNow());
    }
    private async Task<SocketCommandResult> SetAsync(IntegrationDeviceBindingEntity binding, IntegrationSession expected,
        SetSocketPowerCommand request, CancellationToken ct, string? userId)
    {
        await EnsureAccessAsync(ct, userId);
        if (request.CommandId.Value == Guid.Empty || request.DesiredState is not (SwitchState.On or SwitchState.Off))
            throw new ArgumentException("Provide a command identity and an explicit On or Off state.");
        if (_commands.Count >= 256 && !_commands.ContainsKey(binding.Id))
            throw new InvalidOperationException("Socket command capacity is currently unavailable.");
        var gate = _commands.GetOrAdd(binding.Id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var session = await CurrentAsync(binding, expected, ct);
            var desired = request.DesiredState == SwitchState.On;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{binding.Id:D}/{desired}")));
            await using var db = await factory.CreateDbContextAsync(ct);
            await using (var intent = await db.Database.BeginTransactionAsync(ct))
            {
                await EnsureAccessAsync(ct, userId);
                if (!await IntegrationPersistenceGuard.LockCurrentAsync(db, binding.Id, session.ConfigurationRevision, session.Generation, ct))
                    throw new InvalidOperationException("The socket connection changed before the command was recorded.");
                var existing = await db.IntegrationCommands.SingleOrDefaultAsync(c => c.Id == request.CommandId.Value, ct);
                if (existing is not null)
                {
                    if (existing.DeviceId != binding.Id || existing.PayloadHash != hash)
                        throw new InvalidOperationException("A command identity cannot be reused with another payload or device.");
                    return Receipt(existing);
                }
                if (await db.IntegrationCommands.AnyAsync(c => c.DeviceId == binding.Id
                    && (c.Status == "requested" || c.Status == "pending" || c.Status == "uncertain"), ct))
                    throw new InvalidOperationException("This socket has an unresolved command. Check its result before sending another command.");
                var intentCommand = new IntegrationCommandEntity
                {
                    Id = request.CommandId.Value,
                    DeviceId = binding.Id,
                    InstanceId = binding.InstanceId,
                    Revision = session.ConfigurationRevision,
                    Generation = session.Generation,
                    DesiredState = desired,
                    PayloadHash = hash,
                    CreatedAt = clock.GetUtcNow()
                };
                db.IntegrationCommands.Add(intentCommand);
                await db.SaveChangesAsync(ct);
                await intent.CommitAsync(ct);
            }
            var command = db.IntegrationCommands.Local.Single(c => c.Id == request.CommandId.Value);
            // Once intent is durable, cancellation may leave an uncertain remote effect; never replay it.
            if (!Capabilities(binding).CanSwitch)
            {
                command.Status = "rejected";
                command.ErrorCode = "unsupported";
            }
            else try
                {
                    await EnsureAccessAsync(ct, userId);
                    await CurrentAsync(binding, expected, ct);
                    var json = await executor.InvokeAsync(session, "socket.set", IntegrationJson.Element(new
                    {
                        remoteId = binding.RemoteId,
                        channel = binding.Channel,
                        isOn = desired,
                        commandId = request.CommandId.Value.ToString("D")
                    }), ct);
                    var response = json.Deserialize<ProviderSocketCommandResult>(IntegrationJson.Options)
                        ?? throw new InvalidDataException("Missing command acknowledgement.");
                    ApplyResponse(command, response);
                }
                catch (BillingAccessException)
                {
                    command.Status = "rejected";
                    command.ErrorCode = "subscription_required";
                }
                catch (Exception exception)
                {
                    command.Status = "uncertain";
                    command.ErrorCode = exception is OperationCanceledException ? "cancelled_after_intent" : "remote_result_unknown";
                }
            await CompleteAsync(db, binding, session, command);
            Invalidate();
            return Receipt(command);
        }
        finally { gate.Release(); }
    }

    private async Task EnsureAccessAsync(CancellationToken ct, string? userId = null)
    {
        if (billing is null) return;
        if (userId is not null && !(await billing.ReadAsync(userId, ct)).HasAccess) throw new BillingAccessException();
        await using var db = await factory.CreateDbContextAsync(ct);
        await billing.EnsureInstallationAsync(db.InstallationId ?? throw new InvalidOperationException("An installation is required."), ct);
    }
    private static void ApplyResponse(IntegrationCommandEntity command, ProviderSocketCommandResult response)
    {
        if (response.CommandId != command.Id.ToString("D") || response.OperationToken?.Length > 256)
            throw new InvalidDataException("The command acknowledgement has an invalid identity.");
        if (string.Equals(response.Status, "pending", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(response.OperationToken))
            throw new InvalidDataException("A pending command requires an authoritative operation identity.");
        command.Status = response.Status.ToLowerInvariant() switch { "acknowledged" => "acknowledged", "pending" => "pending", "rejected" => "rejected", _ => "uncertain" };
        command.ProviderOperationId = response.OperationToken;
    }
    private async Task CompleteAsync(DeyeSolarDbContext db, IntegrationDeviceBindingEntity binding, IntegrationSession session, IntegrationCommandEntity command)
    {
        command.CompletedAt = command.Status is "pending" or "requested" ? null : clock.GetUtcNow();
        // Bookkeeping is completed after remote acknowledgement even if the HTTP caller disconnected.
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var active = await IntegrationPersistenceGuard.LockCurrentAsync(db, binding.Id, session.ConfigurationRevision, session.Generation, CancellationToken.None);
        if (!active && command.Status == "acknowledged")
        {
            command.Status = "uncertain";
            command.ErrorCode = "retired_generation";
        }
        if (command.Status == "acknowledged" && active)
        {
            foreach (var rule in await db.TriggerRules.Where(r => r.EntityId == binding.Id.ToString("D")).ToListAsync(CancellationToken.None))
            {
                if (rule.CurrentState != command.DesiredState) rule.CurrentStateChangedAt = clock.GetUtcNow().UtcDateTime;
                rule.CurrentState = command.DesiredState;
            }
        }
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }
    private sealed class Socket(DynamicSocketGateway owner, IntegrationDeviceBindingEntity binding,
        IntegrationSession session, string? userId = null) : ISmartSocket
    {
        public SocketId Id => new(binding.Id);
        public SocketCapabilities Capabilities { get; } = DynamicSocketGateway.Capabilities(binding);
        public Task<SocketState> ReadAsync(CancellationToken ct) => owner.ReadAsync(binding, session, ct);
        public Task<SocketCommandResult> SetPowerAsync(SetSocketPowerCommand command, CancellationToken ct)
            => owner.SetAsync(binding, session, command, ct, userId);
    }
    private sealed record InventoryCache(long Request, SocketInventorySnapshot Snapshot, IReadOnlyList<DevicePowerInfo> Legacy);
}
