using System.Reflection;
using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Tests;

public class IntegrationCredentialPasteTests
{
    [Fact]
    public async Task ExplicitPastePublishesOnlyTheCurrentReplaceCredential()
    {
        var (field, clipboard, received) = Create();
        var pending = Paste(field);
        clipboard.Result.SetResult("explicitly-pasted-credential");
        await pending;
        Assert.Equal(new[] { "explicitly-pasted-credential" }, received);
    }

    [Theory]
    [InlineData("field")]
    [InlineData("action")]
    [InlineData("value")]
    [InlineData("busy")]
    [InlineData("disposed")]
    public async Task ADelayedPasteCannotRestoreAnObsoleteCredential(string change)
    {
        var (field, clipboard, received) = Create();
        var pending = Paste(field);
        switch (change)
        {
            case "field": field.Field = new("other-key", "secret", "Other credential", true, Secret: true); break;
            case "action": field.SecretAction = "clear"; break;
            case "value": field.SecretValue = "manually-entered-current-value"; break;
            case "busy": field.Busy = true; break;
            case "disposed": field.Dispose(); break;
        }
        Parameters(field);
        clipboard.Result.SetResult("obsolete-paste");
        await pending;
        Assert.Empty(received);
    }

    private static (IntegrationFieldInput Field, Clipboard Clipboard, List<string> Received) Create()
    {
        var received = new List<string>();
        var field = new IntegrationFieldInput
        {
            Field = new("api-key", "secret", "API key", true, Secret: true),
            SecretAction = "replace",
            SecretValueChanged = EventCallback.Factory.Create<string>(received, (string value) => received.Add(value))
        };
        var clipboard = new Clipboard();
        typeof(IntegrationFieldInput).GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(field, clipboard);
        Parameters(field);
        return (field, clipboard, received);
    }
    private static void Parameters(IntegrationFieldInput field) => typeof(IntegrationFieldInput)
        .GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(field, []);
    private static Task Paste(IntegrationFieldInput field) => (Task)typeof(IntegrationFieldInput)
        .GetMethod("Paste", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(field, [])!;
    private sealed class Clipboard : IJSRuntime
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("smartSolar.paste", identifier);
            return (TValue)(object)await Result.Task;
        }
    }
}
