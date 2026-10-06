using System.Net;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SolarManagement.SmartSockets.Contracts;

namespace DeyeSolar.Web.Tests;

public class SocketCommandControlsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedSendCannotReplayAndOnlyMatchingAcknowledgementPublishesRefresh(bool lostResponse)
    {
        var commands = new Commands { LostResponse = lostResponse };
        var refreshes = 0;
        await using var services = Services(commands);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(commands.DeviceId, () => { ++refreshes; return Task.CompletedTask; });
            Assert.False(renderer.Button(root, "Socket ON").Disabled);
            Assert.Empty(commands.Sends);
            Assert.Equal(0, commands.Reads);
            var send = renderer.Button(root, "Socket ON").EventId;
            await renderer.DispatchAsync(send);
            Assert.Single(commands.Sends);
            Assert.Equal(SwitchState.On, commands.Sends[0].DesiredState);
            Assert.True(renderer.Button(root, "Socket ON").Disabled);
            Assert.True(renderer.Button(root, "Socket OFF").Disabled);
            Assert.Equal(lostResponse, renderer.HasButton(root, "Allow another command"));
            Assert.Equal(0, refreshes);
            Assert.DoesNotContain("switched on", renderer.Text(root), StringComparison.OrdinalIgnoreCase);
            await renderer.DispatchAsync(send);
            Assert.Single(commands.Sends);
            commands.ResultStatus = SocketCommandStatus.Acknowledged;
            await renderer.DispatchAsync(renderer.Button(root, "Check command result").EventId);
            Assert.Equal(commands.Sends[0].CommandId, Assert.Single(commands.Checked));
            Assert.Single(commands.Sends);
            Assert.Equal(1, refreshes);
            Assert.False(renderer.Button(root, "Socket OFF").Disabled);
        });
    }

    [Fact]
    public async Task RecoveryRetainsOriginalCommandAndExplicitReleaseDoesNotClaimAcknowledgement()
    {
        var commands = new Commands();
        var original = new SocketCommandId(Guid.NewGuid());
        commands.Unresolved = [new(commands.DeviceId, original, SwitchState.Off, SocketCommandStatus.Uncertain, DateTimeOffset.UtcNow, null)];
        var refreshes = 0;
        await using var services = Services(commands);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(commands.DeviceId, () => { ++refreshes; return Task.CompletedTask; });
            Assert.True(renderer.Button(root, "Socket ON").Disabled);
            Assert.Empty(commands.Sends);
            Assert.Contains("may still finish", renderer.Text(root));
            Assert.Contains("does not cancel", renderer.Text(root));
            await renderer.DispatchAsync(renderer.Button(root, "Allow another command").EventId);
            Assert.Equal(original, Assert.Single(commands.Released));
            Assert.Contains("previous command result remains unknown", renderer.Text(root));
            Assert.False(renderer.Button(root, "Socket ON").Disabled);
            Assert.Equal(0, refreshes);
            Assert.Empty(commands.Sends);
        });
    }

    [Fact]
    public async Task RecoveredPendingCommandOffersResultCheckingWithoutReleaseOrReplay()
    {
        var commands = new Commands();
        var original = new SocketCommandId(Guid.NewGuid());
        commands.Unresolved = [new(commands.DeviceId, original, SwitchState.Off, SocketCommandStatus.Pending, DateTimeOffset.UtcNow, null)];
        await using var services = Services(commands);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(commands.DeviceId, () => Task.CompletedTask);
            Assert.True(renderer.Button(root, "Socket ON").Disabled);
            Assert.True(renderer.Button(root, "Socket OFF").Disabled);
            Assert.False(renderer.HasButton(root, "Allow another command"));
            Assert.True(renderer.HasButton(root, "Check command result"));
            commands.ResultUnavailable = true;
            await renderer.DispatchAsync(renderer.Button(root, "Check command result").EventId);
            Assert.Contains("command is pending", renderer.Text(root));
            Assert.False(renderer.HasButton(root, "Allow another command"));
            commands.ResultUnavailable = false;
            await renderer.DispatchAsync(renderer.Button(root, "Check command result").EventId);
            Assert.Equal(new[] { original, original }, commands.Checked);
            Assert.Empty(commands.Sends);
            Assert.Empty(commands.Released);
            Assert.Contains("command is pending", renderer.Text(root));
        });
    }

    [Fact]
    public async Task ForeignOrUnavailableRecoveryCannotGrantPermissionToSend()
    {
        foreach (var foreign in new[] { false, true })
        {
            var commands = new Commands { RecoveryUnavailable = !foreign };
            if (foreign) commands.Unresolved = [new(new(Guid.NewGuid()), new(Guid.NewGuid()), SwitchState.On, SocketCommandStatus.Pending, DateTimeOffset.UtcNow, null)];
            await using var services = Services(commands);
            await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.MountAsync(commands.DeviceId, () => Task.CompletedTask);
                Assert.True(renderer.Button(root, "Socket ON").Disabled);
                await renderer.DispatchAsync(renderer.Button(root, "Socket ON").EventId);
                Assert.Empty(commands.Sends);
                Assert.Contains("Command results are unavailable", renderer.Text(root));
            });
        }
    }

    [Fact]
    public async Task ObservationFailureAfterAcknowledgementCannotDowngradeTheConfirmedReceipt()
    {
        var commands = new Commands { ResultStatus = SocketCommandStatus.Acknowledged };
        await using var services = Services(commands);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(commands.DeviceId, () => throw new InvalidOperationException("Inventory unavailable"));
            await renderer.DispatchAsync(renderer.Button(root, "Socket ON").EventId);
            Assert.Contains("provider acknowledged", renderer.Text(root));
            Assert.Contains("next device observation could not be loaded", renderer.Text(root));
            Assert.False(renderer.Button(root, "Socket OFF").Disabled);
            Assert.DoesNotContain("command may have executed", renderer.Text(root));
            Assert.Single(commands.Sends);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedOrUnavailableCapabilitiesKeepControlsBlockedWithoutADeviceCommand(bool unavailable)
    {
        var commands = new Commands
        {
            Capabilities = new(false, true),
            CatalogRead = unavailable ? _ => throw new InvalidOperationException("Unavailable capabilities.") : null
        };
        await using var services = Services(commands);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync(commands.DeviceId, () => Task.CompletedTask);
            Assert.True(renderer.Button(root, "Socket ON").Disabled);
            Assert.True(renderer.Button(root, "Socket OFF").Disabled);
            await renderer.DispatchAsync(renderer.Button(root, "Socket ON").EventId);
            Assert.Empty(commands.Sends);
            Assert.Equal(0, commands.Reads);
            Assert.Contains(unavailable ? "Command results are unavailable" : "does not support switching", renderer.Text(root));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCapabilitiesFromPreviousDeviceCannotChangeCurrentPermission(bool currentCanSwitch)
    {
        var commands = new Commands();
        var replacement = new SocketId(Guid.NewGuid());
        var oldRead = new TaskCompletionSource<ISmartSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.CatalogRead = id => id == commands.DeviceId ? oldRead.Task
            : Task.FromResult<ISmartSocket>(new MetadataOnlySocket(replacement, currentCanSwitch));
        commands.RecoveryRead = _ => [];
        await using var services = Services(commands);
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = 0;
        Task initial = Task.CompletedTask;
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            root = renderer.CreateRoot();
            initial = renderer.SetDeviceAsync(root, commands.DeviceId, () => Task.CompletedTask);
        });
        Task replacementRender = Task.CompletedTask;
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            replacementRender = renderer.SetDeviceAsync(root, replacement, () => Task.CompletedTask);
            Assert.Equal(!currentCanSwitch, renderer.Button(root, "Socket ON").Disabled);
        });
        oldRead.SetResult(new MetadataOnlySocket(commands.DeviceId, !currentCanSwitch));
        await Task.WhenAll(initial, replacementRender).WaitAsync(TimeSpan.FromSeconds(10));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Assert.Equal(!currentCanSwitch, renderer.Button(root, "Socket ON").Disabled);
            Assert.Equal(!currentCanSwitch, renderer.Button(root, "Socket OFF").Disabled);
            Assert.Empty(commands.Sends);
            Assert.Equal(0, commands.Reads);
        });
    }

    private static ServiceProvider Services(Commands commands)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddComponentLocalization();
        services.AddSingleton<IJSRuntime, NullJsRuntime>();
        services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton<ISmartSocketCatalog>(commands);
        services.AddSingleton<ISocketCommandTracker>(commands);
        return services.BuildServiceProvider();
    }

    private sealed class Commands : ISmartSocketCatalog, ISocketCommandTracker, ISmartSocket
    {
        public SocketId DeviceId { get; } = new(Guid.NewGuid());
        public SocketId Id => DeviceId;
        public SocketCapabilities Capabilities { get; init; } = new(true, true);
        public Func<SocketId, Task<ISmartSocket>>? CatalogRead { get; set; }
        public Func<SocketId, IReadOnlyList<SocketCommandReceipt>>? RecoveryRead { get; set; }
        public bool LostResponse { get; init; }
        public bool RecoveryUnavailable { get; init; }
        public bool ResultUnavailable { get; set; }
        public SocketCommandStatus ResultStatus { get; set; } = SocketCommandStatus.Pending;
        public IReadOnlyList<SocketCommandReceipt> Unresolved { get; set; } = [];
        public List<SetSocketPowerCommand> Sends { get; } = [];
        public List<SocketCommandId> Checked { get; } = [];
        public List<SocketCommandId> Released { get; } = [];
        public int Reads { get; private set; }
        public Task<ISmartSocket> GetAsync(SocketId id, CancellationToken ct) { if (CatalogRead is {} read) return read(id); Assert.Equal(DeviceId, id); return Task.FromResult<ISmartSocket>(this); }
        public Task<SocketInventorySnapshot> ReadInventoryAsync(bool forceRefresh, CancellationToken ct) => throw new InvalidOperationException("Command controls cannot infer acknowledgement from inventory.");
        public Task<SocketState> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("Command controls cannot infer acknowledgement from device state.");
        public Task<SocketCommandResult> SetPowerAsync(SetSocketPowerCommand command, CancellationToken ct)
        {
            Sends.Add(command);
            if (LostResponse) throw new InvalidOperationException("Response lost after remote execution.");
            return Task.FromResult(new SocketCommandResult(command.CommandId, ResultStatus, null, null));
        }
        public Task<IReadOnlyList<SocketCommandReceipt>> ListUnresolvedAsync(SocketId deviceId, CancellationToken ct)
        {
            if (RecoveryRead is {} read) return Task.FromResult(read(deviceId));
            Assert.Equal(DeviceId, deviceId);
            if (RecoveryUnavailable) throw new InvalidOperationException("Unavailable journal.");
            return Task.FromResult(Unresolved);
        }
        public Task<SocketCommandResult> ReadResultAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct)
        {
            Assert.Equal(DeviceId, deviceId); ++Reads; Checked.Add(commandId);
            if (ResultUnavailable) throw new InvalidOperationException("Temporary result read failure.");
            return Task.FromResult(new SocketCommandResult(commandId, ResultStatus, null, null));
        }
        public Task<SocketCommandReceipt> ReleaseAsync(SocketId deviceId, SocketCommandId commandId, CancellationToken ct)
        {
            Assert.Equal(DeviceId, deviceId); Released.Add(commandId);
            var receipt = Assert.Single(Unresolved);
            Unresolved = [];
            return Task.FromResult(receipt with { Status = SocketCommandStatus.UncertainClosed });
        }
    }

    private sealed class MetadataOnlySocket(SocketId id, bool canSwitch) : ISmartSocket
    {
        public SocketId Id => id;
        public SocketCapabilities Capabilities { get; } = new(canSwitch, true);
        public Task<SocketState> ReadAsync(CancellationToken ct) => throw new InvalidOperationException("Capability checks must not read a device.");
        public Task<SocketCommandResult> SetPowerAsync(SetSocketPowerCommand command, CancellationToken ct) => throw new InvalidOperationException("Capability checks must not send a command.");
    }

    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        public async Task<int> MountAsync(SocketId id, Func<Task> refresh)
        {
            var root = CreateRoot();
            await SetDeviceAsync(root, id, refresh);
            return root;
        }
        public int CreateRoot() => AssignRootComponentId(InstantiateComponent(typeof(SocketCommandControls)));
        public Task SetDeviceAsync(int root, SocketId id, Func<Task> refresh) =>
            RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(SocketCommandControls.DeviceId)] = id.Value.ToString("D"),
                [nameof(SocketCommandControls.Online)] = true,
                [nameof(SocketCommandControls.OnAcknowledged)] = EventCallback.Factory.Create(this, refresh)
            }));
        public Task DispatchAsync(ulong id) => DispatchEventAsync(id, null, new MouseEventArgs());
        public (ulong EventId, bool Disabled) Button(int root, string label) => Assert.Single(Buttons(root), button => button.Label.Trim() == label).Button;
        public bool HasButton(int root, string label) => Buttons(root).Any(button => button.Label.Trim() == label);
        private IEnumerable<(string Label, (ulong EventId, bool Disabled) Button)> Buttons(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; ++i)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var button in Buttons(frame.ComponentId)) yield return button;
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                var children = frames.Array.Skip(i + 1).Take(frame.ElementSubtreeLength - 1).ToArray();
                var attributes = children.TakeWhile(child => child.FrameType == RenderTreeFrameType.Attribute).ToArray();
                yield return (Text(children).Trim(), (attributes.Single(child => child.AttributeName == "onclick").AttributeEventHandlerId,
                    attributes.Any(child => child.AttributeName == "disabled" && child.AttributeValue is true)));
            }
        }
        public string Text(int id) { var frames = GetCurrentRenderTreeFrames(id); return Text(frames.Array.Take(frames.Count)); }
        private string Text(IEnumerable<RenderTreeFrame> frames) => WebUtility.HtmlDecode(string.Join(" ", frames.Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            RenderTreeFrameType.Component => Text(frame.ComponentId),
            _ => ""
        })));
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
