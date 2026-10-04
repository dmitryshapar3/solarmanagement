using System.Security.Cryptography;
using System.Text.Json;
using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Data;

internal static class RuleConfigurationVersion
{
    public static string Read(TriggerRule rule) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        rule.Id, rule.InstallationId, rule.Name, rule.EntityId, rule.SourceInverterId, rule.Enabled,
        rule.SocTurnOnThreshold, rule.UseSeparateSocTurnOffThreshold, rule.SocTurnOffThreshold,
        rule.UseSolarProductionThreshold, rule.MinAverageSolarProductionWatts, rule.CooldownMinutes,
        rule.IntervalSeconds, rule.ActiveFrom, rule.ActiveTo
    })));

    public static void Check(TriggerRule expected, TriggerRule current)
    {
        if (expected.ConfigurationVersion is { } version && version != Read(current)) throw new RuleConfigurationConflictException();
    }
}

public sealed class RuleConfigurationConflictException() : ArgumentException(
    "The rule changed in another session. Reload it before saving your changes.");
