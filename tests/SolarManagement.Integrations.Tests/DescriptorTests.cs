using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolarManagement.Integrations.Contracts;

namespace SolarManagement.Integrations.Tests;

public sealed class DescriptorTests
{
    [Fact]
    public void SharedConditionFixturesPreserveScalarDefaultsAndFieldGroupSemantics()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shared", "integration-ui-conditions.json")));
        foreach (var test in fixture.RootElement.GetProperty("conditions").EnumerateArray())
        {
            var condition = test.GetProperty("condition").Deserialize<IntegrationUiCondition>(IntegrationJson.Options);
            Assert.True(test.GetProperty("expected").GetBoolean() == IntegrationUiConditions.IsActive(condition, test.GetProperty("values")), test.GetProperty("name").GetString());
        }
        foreach (var test in fixture.RootElement.GetProperty("fields").EnumerateArray())
        {
            var fields = test.GetProperty("fields").Deserialize<IntegrationFieldDescriptor[]>(IntegrationJson.Options)!;
            var key = test.GetProperty("fieldKey").GetString()!;
            var condition = test.TryGetProperty("groupCondition", out var group) ? group.Deserialize<IntegrationUiCondition>(IntegrationJson.Options) : null;
            var descriptor = Flat() with
            {
                Fields = fields,
                UiLayout = new(1, [new("step", "Step", null,
                [new("controls", "Controls", null, fields.Where(field => field.Key != key).Select(field => field.Key).ToArray(), []),
                new("target", "Target", null, [key], [], condition)])])
            };
            Assert.True(test.GetProperty("expected").GetBoolean() == IntegrationUiConditions.IsFieldActive(descriptor, fields.Single(field => field.Key == key), test.GetProperty("values")), test.GetProperty("name").GetString());
        }
    }
    [Fact]
    public void OldFlatDescriptorRetainsExactCanonicalBytesAndDigest()
    {
        const string oldJson = """{"providerId":"test.provider","packageVersion":"1.0.0","packageDigest":"","descriptorDigest":"","displayName":"Fixture","uiContractVersion":1,"configurationVersion":1,"requiredUiFeatures":["text"],"fields":[{"key":"account","kind":"text","label":"Account","required":true,"defaultValue":null,"minimum":null,"maximum":null,"options":null,"secret":false}],"actions":["test","discover"]}""";
        var descriptor = JsonSerializer.Deserialize<IntegrationProviderDescriptor>(oldJson, IntegrationJson.Options)!;
        IntegrationDescriptorValidator.Validate(descriptor);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(descriptor, IntegrationJson.Options);
        Assert.Equal(oldJson, Encoding.UTF8.GetString(serialized));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(oldJson)), SHA256.HashData(serialized));
        Assert.Null(descriptor.UiLayout);
        Assert.Null(descriptor.OAuthDefinition);
    }
    [Fact]
    public void BoundedLayoutReferencesFeaturesAndSecretConditionRulesAreValidated()
    {
        var descriptor = Wizard();
        IntegrationDescriptorValidator.Validate(descriptor);
        var bad = new[]
        {
            descriptor with { RequiredUiFeatures = ["text", "secret"] },
            descriptor with { RequiredUiFeatures = descriptor.RequiredUiFeatures.Append("javascript").ToArray() },
            descriptor with { UiLayout = descriptor.UiLayout! with { Version = 2 } },
            descriptor with { UiLayout = new(1, [new("connect", "Connect", null, [new("first", "First", null, ["account", "account", "apiKey"], [])])]) },
            descriptor with { UiLayout = new(1, [new("connect", "Connect", null, [new("first", "First", null, ["account", "foreign"], [])])]) },
            descriptor with { UiLayout = new(1, [new("connect", "Connect", null, [new("first", "First", null, ["account"], [])])]) },
            descriptor with { Fields = [descriptor.Fields[0], descriptor.Fields[1] with { ActiveWhen = new("apiKey", "present") }] },
            descriptor with { Fields = [descriptor.Fields[0], descriptor.Fields[1] with { ActiveWhen = new("account", "eval", IntegrationJson.Element("yes")) }] },
            descriptor with { Fields = [descriptor.Fields[0] with { ActiveWhen = new("account", "present") }, descriptor.Fields[1]] },
            descriptor with { OAuthDefinition = new(["account"]) },
            descriptor with { OAuthDefinition = new(["apiKey", "apiKey"]) },
            descriptor with { UiLayout = new(1, Enumerable.Range(0, 13).Select(index => new IntegrationUiStep("step" + index, "Step", null, [])).ToArray()) }
        };
        foreach (var value in bad) Assert.Throws<InvalidDataException>(() => IntegrationDescriptorValidator.Validate(value));
    }
    [Fact]
    public void InactiveFieldStillRejectsInvalidSuppliedTypeAndDoesNotMutateRetainedValues()
    {
        var field = new IntegrationFieldDescriptor("channel", "integer", "Channel", true, Minimum: 0, Maximum: 63, ActiveWhen: new("account", "eq", IntegrationJson.Element("active")));
        var descriptor = Flat() with { Fields = [Flat().Fields[0], field], RequiredUiFeatures = ["text", "integer", "conditional-fields"] };
        IntegrationDescriptorValidator.Validate(descriptor);
        var values = IntegrationJson.Element(new { account = "inactive", channel = "wrong" });
        Assert.False(IntegrationUiConditions.IsFieldActive(descriptor, field, values));
        Assert.Throws<InvalidDataException>(() => IntegrationDescriptorValidator.ValidateValue(field, values.GetProperty("channel")));
        Assert.Equal("wrong", values.GetProperty("channel").GetString());
    }
    [Fact]
    public void LayoutOnlyChangesDescriptorDigestWithoutChangingConfigurationVersion()
    {
        var descriptor = Wizard();
        var changed = descriptor with { UiLayout = descriptor.UiLayout! with { Steps = [descriptor.UiLayout.Steps[0] with { Title = "Changed label" }] } };
        IntegrationDescriptorValidator.Validate(changed);
        Assert.Equal(descriptor.ConfigurationVersion, changed.ConfigurationVersion);
        Assert.NotEqual(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(descriptor, IntegrationJson.Options)), SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(changed, IntegrationJson.Options)));
    }
    private static IntegrationProviderDescriptor Flat() => new("test.provider", "1.0.0", "", "", "Fixture", 1, 1, ["text"], [new("account", "text", "Account", true)], ["test", "discover"]);
    internal static IntegrationProviderDescriptor Wizard() => Flat() with
    {
        RequiredUiFeatures = ["text", "secret", "wizard", "groups", "instructions", "conditional-fields", "device-selector", "oauth"],
        Fields = [new("account", "text", "Account", true), new("apiKey", "secret", "Key", true, ActiveWhen: new("account", "present"))],
        Actions = ["test", "discover", "oauth"],
        OAuthDefinition = new(["apiKey"]),
        UiLayout = new(1, [new("connect", "Connect", "Enter your account.", [new("credentials", "Credentials", null, ["account", "apiKey"], ["test", "oauth"]),
            new("devices", "Devices", "Choose devices after connection.", [], ["discover"])])])
    };
}
