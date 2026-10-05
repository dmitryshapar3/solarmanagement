using System.Security.Cryptography;
using System.Text.Json;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;

namespace DeyeSolar.Web.Tests;

public sealed class SharedRuleConfigurationTests
{
    public static IEnumerable<object?[]> ConfigurationVectors()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tests", "fixtures", "rule-configuration-vectors.json"))) root = root.Parent;
        if (root is null) throw new FileNotFoundException("Shared rule configuration vectors were not found.");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "tests", "fixtures", "rule-configuration-vectors.json")));
        foreach (var vector in json.RootElement.EnumerateArray())
        {
            var expected = vector.GetProperty("expectedNormalization");
            yield return [vector.GetProperty("name").GetString(), vector.GetProperty("input").GetRawText(),
                expected.GetProperty("enabled").GetBoolean(), expected.GetProperty("socTurnOffThreshold").GetInt32(),
                expected.GetProperty("minAverageSolarProductionWatts").GetInt32(), vector.GetProperty("expectedError").GetString()];
        }
    }

    [Theory]
    [MemberData(nameof(ConfigurationVectors))]
    public void WebAndDomainUseTheSameDraftContractAsMobile(string name, string input, bool enabled, int socOff, int watts, string? error)
    {
        var request = JsonSerializer.Deserialize<TriggerRuleRequest>(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var rule = request.ToRule();
        RuleConfigurationPolicy.Normalize(rule);
        Assert.Equal(enabled, rule.Enabled);
        Assert.Equal(socOff, rule.SocTurnOffThreshold);
        Assert.Equal(watts, rule.MinAverageSolarProductionWatts);
        Assert.Equal(error, RuleConfigurationPolicy.Validate(rule)?.Code);
    }

    [Fact]
    public void ConfigurationSnapshotKeepsPublishedVersionBytesAndExcludesRuntimeObservations()
    {
        var rule = Rule();
        var expected = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            rule.Id, rule.InstallationId, rule.Name, rule.EntityId, rule.SourceInverterId, rule.Enabled,
            rule.SocTurnOnThreshold, rule.UseSeparateSocTurnOffThreshold, rule.SocTurnOffThreshold,
            rule.UseSolarProductionThreshold, rule.MinAverageSolarProductionWatts, rule.CooldownMinutes,
            rule.IntervalSeconds, rule.ActiveFrom, rule.ActiveTo
        })));
        Assert.Equal(expected, RuleConfigurationVersion.Read(rule));
        rule.CurrentState = !rule.CurrentState;
        rule.CurrentStateChangedAt = DateTime.UtcNow;
        rule.LastEvaluated = DateTime.UtcNow;
        Assert.Equal(expected, RuleConfigurationVersion.Read(rule));
        rule.Name = "Renamed rule";
        Assert.NotEqual(expected, RuleConfigurationVersion.Read(rule));
    }

    [Fact]
    public void ExecutionGuardIgnoresDisplayNameButRejectsChangedPhysicalConfiguration()
    {
        var evaluated = Rule();
        var current = Rule();
        current.Name = "Another display name";
        current.LastEvaluated = DateTime.UtcNow;
        Assert.True(IntegrationAutomationSourceGuard.SameConfiguration(evaluated, current));
        current.MinAverageSolarProductionWatts++;
        Assert.False(IntegrationAutomationSourceGuard.SameConfiguration(evaluated, current));
        current = Rule();
        current.SourceInverterId = Guid.NewGuid();
        Assert.False(IntegrationAutomationSourceGuard.SameConfiguration(evaluated, current));
        current = Rule();
        current.EntityId = Guid.NewGuid().ToString();
        Assert.False(IntegrationAutomationSourceGuard.SameConfiguration(evaluated, current));
    }

    [Fact]
    public void UpdatingConfigurationPreservesOwnershipIdentityAndLiveRuntimeState()
    {
        var current = Rule();
        var before = RuleConfigurationSnapshot.From(current);
        current.CurrentState = true;
        current.CurrentStateChangedAt = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
        current.LastEvaluated = current.CurrentStateChangedAt.Value.AddSeconds(30);
        var incoming = Rule();
        incoming.Id = 999;
        incoming.InstallationId = "foreign-installation";
        incoming.Name = "Updated name";
        incoming.SocTurnOnThreshold = 90;
        RuleConfigurationSnapshot.From(incoming).ApplyTo(current);
        Assert.Equal(before.Id, current.Id);
        Assert.Equal(before.InstallationId, current.InstallationId);
        Assert.Equal(incoming.Name, current.Name);
        Assert.Equal(90, current.SocTurnOnThreshold);
        Assert.True(current.CurrentState);
        Assert.Equal(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), current.CurrentStateChangedAt);
        Assert.Equal(current.CurrentStateChangedAt.Value.AddSeconds(30), current.LastEvaluated);
    }

    private static TriggerRule Rule() => new()
    {
        Id = 7, InstallationId = "installation-a", Name = "Solar surplus",
        EntityId = "b5dce685-8c30-4d48-b26b-4d267b8bf5a1", SourceInverterId = Guid.Parse("f7a8e229-7aa6-4f93-a8b2-eceae2363e0d"),
        Enabled = true, SocTurnOnThreshold = 80, UseSeparateSocTurnOffThreshold = true,
        SocTurnOffThreshold = 60, UseSolarProductionThreshold = true, MinAverageSolarProductionWatts = 3000,
        CooldownMinutes = 15, IntervalSeconds = 30, ActiveFrom = new(22, 0), ActiveTo = new(6, 0)
    };
}
