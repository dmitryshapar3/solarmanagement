using DeyeSolar.Web.Components.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeyeSolar.Web.Tests;

public sealed class UiOptionalIntegerTests
{
    [Theory]
    [InlineData("2.5")]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("not a number")]
    public async Task InvalidInputKeepsTheLastCountAndBlocksTheFormUntilCorrected(string text)
    {
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddComponentLocalization();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var host = new Host(); var root = await renderer.Mount(host);
            Assert.Null(host.Value); Assert.True(host.Form.IsValid);
            await renderer.Input(root, "9");
            Assert.Equal(9, host.Value); Assert.True(host.Form.IsValid);
            await renderer.Input(root, text);
            Assert.Equal(9, host.Value); Assert.False(host.Form.IsValid);
            Assert.True(renderer.Invalid(root));
            await renderer.Input(root, "0");
            Assert.Equal(0, host.Value); Assert.True(host.Form.IsValid);
            await renderer.Input(root, "");
            Assert.Null(host.Value); Assert.True(host.Form.IsValid);
        });
    }

    [Fact]
    public async Task DisabledFieldCannotUpdateTheCountEvenIfAnEventIsDispatched()
    {
        var collection = new ServiceCollection(); collection.AddLogging(); collection.AddComponentLocalization();
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var host = new Host { Disabled = true }; var root = await renderer.Mount(host);
            Assert.True(renderer.Disabled(root));
            await renderer.Input(root, "8");
            Assert.Null(host.Value); Assert.True(host.Form.IsValid);
        });
    }

    private sealed class Host : ComponentBase
    {
        public int? Value { get; private set; }
        public bool Disabled { get; init; }
        public UiFormState Form { get; } = new();
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<UiFormState>>(0); builder.AddAttribute(1, "Value", Form);
            builder.AddAttribute(2, "IsFixed", true); builder.AddAttribute(3, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<UiOptionalInteger>(0); child.AddAttribute(1, "Label", "Panel count");
                child.AddAttribute(2, "Value", Value); child.AddAttribute(3, "Disabled", Disabled);
                child.AddAttribute(4, "ValueChanged", EventCallback.Factory.Create<int?>(this, value => Value = value)); child.CloseComponent();
            })); builder.CloseComponent();
        }
    }
    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory logs) : Renderer(services, logs)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception error) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        public async Task<int> Mount(Host host) { var id = AssignRootComponentId(host); await RenderRootComponentAsync(id, ParameterView.Empty); return id; }
        private RenderTreeFrame[] InputFrames(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (var i = 0; i < frames.Count; ++i)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "input")
                    return frames.Array.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
                if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    var child = InputFrames(frame.ComponentId); if (child.Length > 0) return child;
                }
            }
            return [];
        }
        public Task Input(int root, string value) => DispatchEventAsync(Assert.Single(InputFrames(root), f => f.AttributeName == "oninput").AttributeEventHandlerId, null, new ChangeEventArgs { Value = value });
        public bool Invalid(int root) => Assert.Single(InputFrames(root), f => f.AttributeName == "aria-invalid").AttributeValue?.ToString() == "true";
        public bool Disabled(int root) => InputFrames(root).Any(f => f.AttributeName == "disabled" && f.AttributeValue is true);
    }
}
