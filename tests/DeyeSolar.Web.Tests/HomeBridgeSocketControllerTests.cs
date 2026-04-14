using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeyeSolar.Web.Tests;

public class HomeBridgeSocketControllerTests
{
    [Fact]
    public async Task TurnOnAsync_Completes_WhenBridgeReportsSuccess()
    {
        var dbFactory = TestDbFactory.Create(nameof(TurnOnAsync_Completes_WhenBridgeReportsSuccess));
        var bridgeOptions = new TestOptionsMonitor<SocketBackendOptions>(new SocketBackendOptions
        {
            BridgeId = "home-main",
            CommandTimeoutSeconds = 2
        });
        var controller = CreateController(dbFactory, bridgeOptions);

        var completionTask = CompleteNextCommandAsync(dbFactory, BridgeCommandStatuses.Succeeded, null);

        await controller.TurnOnAsync("device-1", CancellationToken.None);
        await completionTask;

        await using var db = await dbFactory.CreateDbContextAsync();
        var command = await db.BridgeCommands.SingleAsync();
        Assert.Equal(BridgeCommandTypes.SetState, command.CommandType);
        Assert.True(command.DesiredState);
        Assert.Equal(BridgeCommandStatuses.Succeeded, command.Status);
    }

    [Fact]
    public async Task TurnOnAsync_Throws_WhenBridgeReportsFailure()
    {
        var dbFactory = TestDbFactory.Create(nameof(TurnOnAsync_Throws_WhenBridgeReportsFailure));
        var bridgeOptions = new TestOptionsMonitor<SocketBackendOptions>(new SocketBackendOptions
        {
            BridgeId = "home-main",
            CommandTimeoutSeconds = 2
        });
        var controller = CreateController(dbFactory, bridgeOptions);

        var completionTask = CompleteNextCommandAsync(dbFactory, BridgeCommandStatuses.Failed, "socket offline");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TurnOnAsync("device-1", CancellationToken.None));
        await completionTask;

        Assert.Contains("socket offline", ex.Message);
    }

    [Fact]
    public async Task TurnOnAsync_Throws_WhenBridgeTimesOut()
    {
        var dbFactory = TestDbFactory.Create(nameof(TurnOnAsync_Throws_WhenBridgeTimesOut));
        var bridgeOptions = new TestOptionsMonitor<SocketBackendOptions>(new SocketBackendOptions
        {
            BridgeId = "home-main",
            CommandTimeoutSeconds = 1
        });
        var controller = CreateController(dbFactory, bridgeOptions);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TurnOnAsync("device-1", CancellationToken.None));

        Assert.Contains("Timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStateAsync_Throws_WhenStateIsStale()
    {
        var dbFactory = TestDbFactory.Create(nameof(GetStateAsync_Throws_WhenStateIsStale));
        var bridgeOptions = new TestOptionsMonitor<SocketBackendOptions>(new SocketBackendOptions
        {
            BridgeId = "home-main",
            StateStaleAfterSeconds = 30
        });

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.BridgeDeviceShadows.Add(new BridgeDeviceShadow
            {
                BridgeId = "home-main",
                DeviceId = "device-1",
                Name = "Socket",
                Online = true,
                IsOn = true,
                LastSeenAt = DateTime.UtcNow.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        var controller = CreateController(dbFactory, bridgeOptions);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.GetStateAsync("device-1", CancellationToken.None));

        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HomeBridgeSocketController CreateController(
        IDbContextFactory<DeyeSolarDbContext> dbFactory,
        TestOptionsMonitor<SocketBackendOptions> bridgeOptions)
    {
        var bridgeState = new BridgeStateService(
            dbFactory,
            new DeviceStatusSnapshot(),
            bridgeOptions,
            NullLogger<BridgeStateService>.Instance);

        return new HomeBridgeSocketController(
            bridgeState,
            bridgeOptions,
            new TestOptionsMonitor<TuyaOptions>(new TuyaOptions()));
    }

    private static Task CompleteNextCommandAsync(
        IDbContextFactory<DeyeSolarDbContext> dbFactory,
        string status,
        string? message)
    {
        return Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var command = await db.BridgeCommands.SingleOrDefaultAsync();
                if (command != null)
                {
                    command.Status = status;
                    command.ResultMessage = message;
                    command.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("Command was never enqueued for completion.");
        });
    }
}
