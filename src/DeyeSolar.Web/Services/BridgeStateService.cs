using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

public record BridgeCommandWaitResult(string Status, string? ResultMessage);

public class BridgeStateService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly IDbContextFactory<DeyeSolarDbContext> _dbFactory;
    private readonly DeviceStatusSnapshot _deviceStatusSnapshot;
    private readonly IOptionsMonitor<SocketBackendOptions> _backendOptions;
    private readonly ILogger<BridgeStateService> _logger;

    public BridgeStateService(
        IDbContextFactory<DeyeSolarDbContext> dbFactory,
        DeviceStatusSnapshot deviceStatusSnapshot,
        IOptionsMonitor<SocketBackendOptions> backendOptions,
        ILogger<BridgeStateService> logger)
    {
        _dbFactory = dbFactory;
        _deviceStatusSnapshot = deviceStatusSnapshot;
        _backendOptions = backendOptions;
        _logger = logger;
    }

    public async Task<Guid> EnqueueSetStateCommandAsync(
        string bridgeId,
        string deviceId,
        bool desiredState,
        CancellationToken ct)
    {
        var command = new BridgeCommand
        {
            Id = Guid.NewGuid(),
            BridgeId = bridgeId,
            DeviceId = deviceId,
            CommandType = BridgeCommandTypes.SetState,
            DesiredState = desiredState,
            Status = BridgeCommandStatuses.Queued,
            RequestedAt = DateTime.UtcNow
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.BridgeCommands.Add(command);
        await db.SaveChangesAsync(ct);
        return command.Id;
    }

    public async Task<Guid> EnqueueRefreshInventoryCommandAsync(string bridgeId, CancellationToken ct)
    {
        var command = new BridgeCommand
        {
            Id = Guid.NewGuid(),
            BridgeId = bridgeId,
            DeviceId = string.Empty,
            CommandType = BridgeCommandTypes.RefreshInventory,
            Status = BridgeCommandStatuses.Queued,
            RequestedAt = DateTime.UtcNow
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.BridgeCommands.Add(command);
        await db.SaveChangesAsync(ct);
        return command.Id;
    }

    public async Task<BridgeCommandWaitResult> WaitForCommandCompletionAsync(
        Guid commandId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow <= deadline)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var command = await db.BridgeCommands
                .AsNoTracking()
                .Where(c => c.Id == commandId)
                .Select(c => new { c.Status, c.ResultMessage })
                .SingleOrDefaultAsync(ct);

            if (command == null)
                throw new InvalidOperationException($"Bridge command {commandId} was not found.");

            if (command.Status is BridgeCommandStatuses.Succeeded or BridgeCommandStatuses.Failed)
                return new BridgeCommandWaitResult(command.Status, command.ResultMessage);

            await Task.Delay(WaitPollInterval, ct);
        }

        return new BridgeCommandWaitResult(
            "timed_out",
            "Timed out waiting for the home bridge to complete the command.");
    }

    public async Task<BridgeDeviceShadow?> GetDeviceShadowAsync(string bridgeId, string deviceId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.BridgeDeviceShadows
            .AsNoTracking()
            .SingleOrDefaultAsync(d => d.BridgeId == bridgeId && d.DeviceId == deviceId, ct);
    }

    public async Task<List<BridgeDeviceShadow>> GetDeviceShadowsAsync(string bridgeId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.BridgeDeviceShadows
            .AsNoTracking()
            .Where(d => d.BridgeId == bridgeId)
            .OrderBy(d => d.Name)
            .ThenBy(d => d.DeviceId)
            .ToListAsync(ct);
    }

    public async Task<BridgeHeartbeatInfo?> GetHeartbeatAsync(string bridgeId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var heartbeat = await db.BridgeHeartbeats
            .AsNoTracking()
            .SingleOrDefaultAsync(h => h.BridgeId == bridgeId, ct);

        return heartbeat == null
            ? null
            : new BridgeHeartbeatInfo(
                heartbeat.BridgeId,
                new DateTimeOffset(heartbeat.LastSeenAt, TimeSpan.Zero),
                heartbeat.BridgeVersion,
                heartbeat.HostName);
    }

    public async Task<BridgeSyncResponse> SyncAsync(BridgeSyncRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        foreach (var completed in request.CompletedCommands)
        {
            var command = await db.BridgeCommands
                .SingleOrDefaultAsync(c => c.Id == completed.CommandId && c.BridgeId == request.BridgeId, ct);

            if (command == null || command.Status is BridgeCommandStatuses.Succeeded or BridgeCommandStatuses.Failed)
                continue;

            command.Status = completed.Success ? BridgeCommandStatuses.Succeeded : BridgeCommandStatuses.Failed;
            command.CompletedAt = now;
            command.ResultMessage = completed.Message;
            command.LeaseExpiresAt = null;
        }

        var heartbeat = await db.BridgeHeartbeats
            .SingleOrDefaultAsync(h => h.BridgeId == request.BridgeId, ct);

        if (heartbeat == null)
        {
            heartbeat = new BridgeHeartbeat { BridgeId = request.BridgeId };
            db.BridgeHeartbeats.Add(heartbeat);
        }

        heartbeat.LastSeenAt = now;
        heartbeat.BridgeVersion = request.Heartbeat.BridgeVersion;
        heartbeat.HostName = request.Heartbeat.HostName;

        foreach (var device in request.Devices)
        {
            var existing = await db.BridgeDeviceShadows
                .SingleOrDefaultAsync(
                    d => d.BridgeId == request.BridgeId && d.DeviceId == device.DeviceId,
                    ct);

            if (existing == null)
            {
                existing = new BridgeDeviceShadow
                {
                    BridgeId = request.BridgeId,
                    DeviceId = device.DeviceId
                };
                db.BridgeDeviceShadows.Add(existing);
            }

            existing.Name = device.Name;
            existing.Category = device.Category;
            existing.Online = device.Online;
            existing.IsOn = device.IsOn;
            existing.CurrentPowerW = device.CurrentPowerW;
            existing.LastSeenAt = now;
            existing.Error = device.Error;
        }

        var reportedDeviceIds = request.Devices
            .Select(d => d.DeviceId)
            .ToHashSet(StringComparer.Ordinal);
        var removedDevices = await db.BridgeDeviceShadows
            .Where(d => d.BridgeId == request.BridgeId && !reportedDeviceIds.Contains(d.DeviceId))
            .ToListAsync(ct);
        if (removedDevices.Count > 0)
            db.BridgeDeviceShadows.RemoveRange(removedDevices);

        var pendingCommands = await db.BridgeCommands
            .Where(c => c.BridgeId == request.BridgeId &&
                        (c.Status == BridgeCommandStatuses.Queued ||
                         (c.Status == BridgeCommandStatuses.Leased &&
                          c.LeaseExpiresAt.HasValue &&
                          c.LeaseExpiresAt.Value <= now)))
            .OrderBy(c => c.RequestedAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var command in pendingCommands)
        {
            command.Status = BridgeCommandStatuses.Leased;
            command.LeasedAt = now;
            command.LeaseExpiresAt = now.Add(LeaseDuration);
        }

        await db.SaveChangesAsync(ct);
        UpdateLiveSnapshotIfActive(request);

        _logger.LogDebug(
            "Bridge sync {BridgeId}: {Devices} devices, {Completed} completed, {Pending} leased",
            request.BridgeId,
            request.Devices.Count,
            request.CompletedCommands.Count,
            pendingCommands.Count);

        return new BridgeSyncResponse
        {
            PendingCommands = pendingCommands
                .Select(c => new BridgePendingCommand
                {
                    CommandId = c.Id,
                    DeviceId = c.DeviceId,
                    CommandType = c.CommandType,
                    DesiredState = c.DesiredState
                })
                .ToList()
        };
    }

    private void UpdateLiveSnapshotIfActive(BridgeSyncRequest request)
    {
        var options = _backendOptions.CurrentValue;
        if (!string.Equals(options.BridgeId, request.BridgeId, StringComparison.Ordinal))
        {
            return;
        }

        var devices = request.Devices
            .Select(d => new DevicePowerInfo(
                d.DeviceId,
                d.Name,
                d.Category,
                d.Online,
                d.IsOn,
                d.CurrentPowerW))
            .ToList();

        _deviceStatusSnapshot.Update(devices);
    }
}
