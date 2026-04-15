using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeyeSolar.Web.Tests;

public class BridgeStateServiceTests
{
    [Fact]
    public async Task SyncAsync_UpsertsDevices_CompletesCommands_AndReturnsPendingForBridgeOnly()
    {
        var dbFactory = TestDbFactory.Create(nameof(SyncAsync_UpsertsDevices_CompletesCommands_AndReturnsPendingForBridgeOnly));
        var options = new TestOptionsMonitor<SocketBackendOptions>(new SocketBackendOptions
        {
            BridgeId = "home-main"
        });

        Guid completedCommandId;
        Guid pendingCommandId;

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            completedCommandId = Guid.NewGuid();
            pendingCommandId = Guid.NewGuid();

            db.BridgeCommands.AddRange(
                new BridgeCommand
                {
                    Id = completedCommandId,
                    BridgeId = "home-main",
                    DeviceId = "device-1",
                    CommandType = BridgeCommandTypes.SetState,
                    DesiredState = true,
                    Status = BridgeCommandStatuses.Leased,
                    RequestedAt = DateTime.UtcNow.AddSeconds(-5),
                    LeasedAt = DateTime.UtcNow.AddSeconds(-5),
                    LeaseExpiresAt = DateTime.UtcNow.AddSeconds(10)
                },
                new BridgeCommand
                {
                    Id = pendingCommandId,
                    BridgeId = "home-main",
                    DeviceId = "device-2",
                    CommandType = BridgeCommandTypes.RefreshInventory,
                    Status = BridgeCommandStatuses.Queued,
                    RequestedAt = DateTime.UtcNow
                },
                new BridgeCommand
                {
                    Id = Guid.NewGuid(),
                    BridgeId = "other-bridge",
                    DeviceId = "device-x",
                    CommandType = BridgeCommandTypes.SetState,
                    DesiredState = false,
                    Status = BridgeCommandStatuses.Queued,
                    RequestedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var snapshot = new DeviceStatusSnapshot();
        var bridgeState = new BridgeStateService(
            dbFactory,
            snapshot,
            options,
            NullLogger<BridgeStateService>.Instance);

        var response = await bridgeState.SyncAsync(
            new BridgeSyncRequest
            {
                BridgeId = "home-main",
                Heartbeat = new BridgeHeartbeatReport
                {
                    BridgeVersion = "1.0.0",
                    HostName = "test-host"
                },
                Devices =
                [
                    new BridgeDeviceReport
                    {
                        DeviceId = "device-2",
                        Name = "Desk Plug",
                        Category = "cz",
                        Online = true,
                        IsOn = false,
                        CurrentPowerW = 15
                    }
                ],
                CompletedCommands =
                [
                    new BridgeCommandResultReport
                    {
                        CommandId = completedCommandId,
                        Success = true,
                        Message = "done"
                    }
                ]
            },
            CancellationToken.None);

        Assert.Single(response.PendingCommands);
        Assert.Equal(pendingCommandId, response.PendingCommands[0].CommandId);

        await using var assertDb = await dbFactory.CreateDbContextAsync();
        var completed = await assertDb.BridgeCommands.SingleAsync(c => c.Id == completedCommandId);
        var pending = await assertDb.BridgeCommands.SingleAsync(c => c.Id == pendingCommandId);
        var device = await assertDb.BridgeDeviceShadows.SingleAsync(d => d.BridgeId == "home-main" && d.DeviceId == "device-2");
        var heartbeat = await assertDb.BridgeHeartbeats.SingleAsync(h => h.BridgeId == "home-main");

        Assert.Equal(BridgeCommandStatuses.Succeeded, completed.Status);
        Assert.Equal(BridgeCommandStatuses.Leased, pending.Status);
        Assert.Equal("Desk Plug", device.Name);
        Assert.Equal("test-host", heartbeat.HostName);

        var snapshotDevice = Assert.Single(snapshot.Current!);
        Assert.Equal("device-2", snapshotDevice.Id);
    }
}
