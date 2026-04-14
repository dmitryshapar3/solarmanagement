using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeyeSolar.Web.Tests;

public class HomeBridgeInventoryServiceTests
{
    [Fact]
    public async Task GetCachedDevicesAsync_ReturnsStoredDevices()
    {
        var dbFactory = TestDbFactory.Create(nameof(GetCachedDevicesAsync_ReturnsStoredDevices));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.BridgeDeviceShadows.Add(new BridgeDeviceShadow
            {
                BridgeId = "home-main",
                DeviceId = "device-1",
                Name = "Kitchen Plug",
                Category = "cz",
                Online = true,
                IsOn = true,
                CurrentPowerW = 42,
                LastSeenAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var inventory = CreateInventory(dbFactory, new SocketBackendOptions { BridgeId = "home-main" });
        var devices = await inventory.GetCachedDevicesAsync(CancellationToken.None);

        var device = Assert.Single(devices);
        Assert.Equal("device-1", device.Id);
        Assert.Equal("Kitchen Plug", device.Name);
        Assert.True(device.Online);
        Assert.Equal(42, device.CurrentPowerW);
    }

    [Fact]
    public async Task RefreshDevicesAsync_WaitsForBridgeAndReturnsUpdatedDevices()
    {
        var dbFactory = TestDbFactory.Create(nameof(RefreshDevicesAsync_WaitsForBridgeAndReturnsUpdatedDevices));
        var options = new SocketBackendOptions
        {
            BridgeId = "home-main",
            CommandTimeoutSeconds = 2
        };
        var inventory = CreateInventory(dbFactory, options);

        var completionTask = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var command = await db.BridgeCommands
                    .SingleOrDefaultAsync(c => c.CommandType == BridgeCommandTypes.RefreshInventory);

                if (command != null)
                {
                    db.BridgeDeviceShadows.Add(new BridgeDeviceShadow
                    {
                        BridgeId = "home-main",
                        DeviceId = "device-2",
                        Name = "Desk Plug",
                        Category = "cz",
                        Online = true,
                        IsOn = false,
                        CurrentPowerW = 0,
                        LastSeenAt = DateTime.UtcNow
                    });
                    command.Status = BridgeCommandStatuses.Succeeded;
                    command.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("Refresh command was never enqueued.");
        });

        var devices = await inventory.RefreshDevicesAsync(CancellationToken.None);
        await completionTask;

        var device = Assert.Single(devices);
        Assert.Equal("device-2", device.Id);
    }

    private static HomeBridgeInventoryService CreateInventory(
        IDbContextFactory<DeyeSolarDbContext> dbFactory,
        SocketBackendOptions options)
    {
        var bridgeOptions = new TestOptionsMonitor<SocketBackendOptions>(options);
        var bridgeState = new BridgeStateService(
            dbFactory,
            new DeviceStatusSnapshot(),
            bridgeOptions,
            NullLogger<BridgeStateService>.Instance);

        return new HomeBridgeInventoryService(bridgeState, bridgeOptions);
    }
}
