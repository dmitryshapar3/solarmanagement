using System.Text.RegularExpressions;
using DeyeSolar.Web.Integrations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Tests;

public class IntegrationFieldMarkupTests
{
    [Theory]
    [InlineData("text", false)]
    [InlineData("integer", false)]
    [InlineData("boolean", false)]
    [InlineData("select", false)]
    [InlineData("secret", false)]
    [InlineData("text", true)]
    [InlineData("integer", true)]
    [InlineData("boolean", true)]
    [InlineData("select", true)]
    [InlineData("secret", true)]
    public async Task ProviderControlsAreEditableAtRestAndDisabledOnlyWhileAnOperationIsPending(string kind, bool busy)
    {
        var html = await Render(kind, busy, required: false);
        var controls = Regex.Matches(html, @"<(?:input|select)\b[^>]*>", RegexOptions.IgnoreCase).Select(match => match.Value).ToArray();
        Assert.NotEmpty(controls);
        Assert.All(controls, control => Assert.Equal(busy, Regex.IsMatch(control, @"\sdisabled(?:\s|=|>)")));
        Assert.DoesNotContain("disabled=\"Busy\"", html);
    }

    [Theory]
    [InlineData("text", false)]
    [InlineData("text", true)]
    [InlineData("boolean", false)]
    [InlineData("boolean", true)]
    [InlineData("select", false)]
    [InlineData("select", true)]
    public async Task NativeValidationUsesTheDescriptorRequiredFlagAndLeavesOptionalFieldsOptional(string kind, bool required)
    {
        var html = await Render(kind, busy: false, required);
        var control = Regex.Match(html, @"<(?:input|select)\b[^>]*>", RegexOptions.IgnoreCase).Value;
        Assert.Equal(required, Regex.IsMatch(control, @"\srequired(?:\s|=|>)"));
        Assert.DoesNotContain("Field.Required", html);
    }

    private static async Task<string> Render(string kind, bool busy, bool required)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddComponentLocalization();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<IntegrationFieldInput>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(IntegrationFieldInput.Field)] = new IntegrationFieldDescriptor("setting", kind, "Provider setting", required,
                    Secret: kind == "secret", Options: kind == "select" ? new[] { new IntegrationSelectOption("a", "A") } : null),
                [nameof(IntegrationFieldInput.Busy)] = busy,
                [nameof(IntegrationFieldInput.SecretAction)] = "replace"
            }));
            return output.ToHtmlString();
        });
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
