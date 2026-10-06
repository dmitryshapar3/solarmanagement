using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Integrations;

namespace DeyeSolar.Web.Redesign;

public sealed record ActivityItemDto(long Id, DateTimeOffset Start, DateTimeOffset End, string Kind,
    int? RuleId, string? RuleName, string? DeviceId, string? ReasonCode, bool? State, int CheckCount,
    int? SocMin, int? SocMax, int? SolarMinWatts, int? SolarMaxWatts, string? ActorUserId, string? Client,
    string? ActorDisplayName = null);
public sealed record ActivitySummaryDto(int Switches, int ConfirmedCommands, double? OnSeconds,
    double KnownSeconds, double ExpectedSeconds, bool Partial);
public sealed record ActivityFeedDto(DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<ActivityItemDto> Items, string? NextCursor, ActivitySummaryDto Summary);
public sealed record ActivityCheckDto(long Id, DateTimeOffset OccurredAt, string Kind, string? ReasonCode,
    int? BatterySoc, int? SolarWatts, bool? State, string? ConfigurationVersion, long? Generation);
public sealed record ActivityChecksDto(IReadOnlyList<ActivityCheckDto> Items, string? NextCursor);
public sealed record DeviceIntervalDto(DateTimeOffset From, DateTimeOffset To, bool? IsOn, string Evidence);
public sealed record DeviceHistoryDto(DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<DeviceIntervalDto> Intervals, double? OnSeconds, double KnownSeconds, bool Partial);
public sealed record ReadingRowDto(int Id, DateTimeOffset Timestamp, Guid? InverterId, long ConfigurationRevision,
    long RuntimeGeneration, string DataSource, double? BatterySoc, double? BatteryTemperature,
    double? BatteryVoltage, double? BatteryPower, double? BatteryCurrent, double? SolarProduction,
    double? GridConsumption, double? LoadPower, DateTimeOffset? SolarObservedAt);
public sealed record ReadingGapDto(DateTimeOffset From, DateTimeOffset To, string ReasonCode);
public sealed record ReadingsViewDto(DateTimeOffset Start, DateTimeOffset End, string Aggregate,
    IReadOnlyList<ReadingRowDto> Items, IReadOnlyList<ReadingGapDto> Gaps, string? NextCursor, bool Partial);
public sealed record EvaluationConditionDto(string Kind, double? Observed, double? Threshold,
    string Status, string? ReasonCode);
public sealed record RuleEvaluationDto(int RuleId, string? ConfigurationVersion, DateTimeOffset? CheckedAt,
    DateTimeOffset? NextCheckAt, IReadOnlyList<EvaluationConditionDto> Conditions, string Decision,
    string State, string Freshness, Guid? SourceInverterId);

/// <summary>Read-only scoped presentation queries; callers in a Blazor circuit reauthorize each query.</summary>
public sealed partial class RedesignQueries(IDbContextFactory<DeyeSolarDbContext> factory,
    IConfigurationRules rules, TimeProvider clock, InteractiveSecurityContext security,
    DeviceStatusSnapshot devices, DeviceNameService deviceNames, SolarEstimateService solarEstimate,
    SolarManagement.Integrations.Contracts.IIntegrationProviderCatalog? catalog = null)
{
    public async Task<ActivityFeedDto> ActivityAsync(DateTimeOffset? from = null, DateTimeOffset? to = null,
        int? ruleId = null, bool changesOnly = false, string? cursor = null, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var filter = $"activity/{ruleId}/{changesOnly}";
        var page = await WindowAsync(db, from, to, 168, 720, cursor, filter, ct);
        var events = db.ActivityEvents.AsNoTracking().Where(e => e.Id <= page.Snapshot
            && e.OccurredAt >= page.From && e.OccurredAt < page.Through && e.Kind != "device.observed");
        if (ruleId.HasValue) events = events.Where(e => e.RuleId == ruleId);
        if (changesOnly) events = events.Where(e => e.Kind != "rule.checked");
        var groups = events.GroupBy(e => e.GroupId ?? e.Id).Select(g => new
        {
            Id = g.Key, Start = g.Min(e => e.OccurredAt), End = g.Max(e => e.OccurredAt),
            Kind = g.Max(e => e.Kind), RuleId = g.Max(e => e.RuleId), RuleName = g.Max(e => e.RuleName),
            DeviceId = g.Max(e => e.DeviceId), Reason = g.Max(e => e.ReasonCode),
            State = g.Max(e => e.State == true ? 1 : e.State == false ? 0 : (int?)null),
            Count = g.Count(), SocMin = g.Min(e => e.BatterySoc), SocMax = g.Max(e => e.BatterySoc),
            PvMin = g.Min(e => e.SolarWatts), PvMax = g.Max(e => e.SolarWatts),
            Actor = g.Max(e => e.ActorUserId), Client = g.Max(e => e.Client)
        });
        if (page.Before.HasValue) groups = groups.Where(g => g.End < page.Before
            || g.End == page.Before && g.Id < page.BeforeId);
        var found = await groups.OrderByDescending(g => g.End).ThenByDescending(g => g.Id).Take(101).ToListAsync(ct);
        var items = found.Take(100).Select(g => new ActivityItemDto(g.Id, Utc(g.Start), Utc(g.End), g.Kind!,
            g.RuleId, g.RuleName, g.DeviceId, g.Reason, g.State.HasValue ? g.State == 1 : null,
            g.Count, g.SocMin, g.SocMax, g.PvMin, g.PvMax, g.Actor, g.Client)).ToArray();
        var actorIds = items.Select(item => item.ActorUserId).OfType<string>().Distinct().ToArray();
        var actorNames = await (from claim in db.UserClaims.AsNoTracking()
            join member in db.InstallationMemberships.AsNoTracking() on claim.UserId equals member.UserId
            where member.InstallationId == db.InstallationId && actorIds.Contains(claim.UserId)
                && claim.ClaimType == AccountManagementService.DisplayNameClaim
            select new { claim.UserId, claim.ClaimValue }).ToListAsync(ct);
        var names = actorNames.Where(actor => !string.IsNullOrWhiteSpace(actor.ClaimValue)
                && actor.ClaimValue.Length <= 80 && !actor.ClaimValue.Any(char.IsControl))
            .GroupBy(actor => actor.UserId).ToDictionary(group => group.Key, group => group.First().ClaimValue!.Trim());
        items = items.Select(item => item with { ActorDisplayName = item.ActorUserId is {} actor ? names.GetValueOrDefault(actor) : null }).ToArray();
        var summaryEvents = db.ActivityEvents.AsNoTracking().Where(e => e.Id <= page.Snapshot
            && e.OccurredAt >= page.From && e.OccurredAt < page.Through);
        if (ruleId.HasValue) summaryEvents = summaryEvents.Where(e => e.RuleId == ruleId);
        var switched = await summaryEvents.CountAsync(e => e.Kind == "rule.switched"
            || e.Kind == "command.result" && e.ReasonCode == "acknowledged" && e.ActorUserId != null, ct);
        var confirmed = await summaryEvents.CountAsync(e => e.Kind == "command.result" && e.ReasonCode == "acknowledged", ct);
        var states = await db.ActivityEvents.AsNoTracking().Where(e => e.Kind == "device.observed"
            && e.OccurredAt >= page.From.AddMinutes(-10) && e.OccurredAt < page.Through && e.Id <= page.Snapshot)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToListAsync(ct);
        string[] deviceIds;
        if (ruleId.HasValue)
        {
            var target = await db.TriggerRules.Where(r => r.Id == ruleId).Select(r => r.EntityId).SingleOrDefaultAsync(ct);
            deviceIds = target is null
                ? await summaryEvents.Where(e => e.DeviceId != null).Select(e => e.DeviceId!).Distinct().ToArrayAsync(ct)
                : [target];
            states = states.Where(e => e.DeviceId is not null && deviceIds.Contains(e.DeviceId)).ToList();
        }
        else
        {
            var bound = await db.IntegrationDeviceBindings.Where(b => b.Kind == "socket" && b.Enabled).Select(b => b.Id).ToArrayAsync(ct);
            deviceIds = bound.Select(id => id.ToString("D")).Concat(states.Select(e => e.DeviceId).OfType<string>()).Distinct(StringComparer.Ordinal).ToArray();
        }
        var histories = deviceIds.Select(id => BuildDeviceHistory(states.Where(e => e.DeviceId == id), Utc(page.From), Utc(page.Through))).ToArray();
        var known = histories.Sum(h => h.KnownSeconds);
        var expected = (page.Through - page.From).TotalSeconds * histories.Length;
        var last = items.LastOrDefault();
        var next = found.Count > 100 && last is not null ? Encode(page with { Before = last.End.UtcDateTime, BeforeId = last.Id }) : null;
        return new(Utc(page.From), Utc(page.Through), items, next, new(switched, confirmed,
            known > 0 ? histories.Sum(h => h.OnSeconds ?? 0) : null, known, expected,
            histories.Length == 0 || histories.Any(h => h.Partial)));
    }

    public async Task<ActivityChecksDto> ChecksAsync(long groupId, string? cursor = null, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        if (groupId <= 0) throw new ArgumentException("Choose a valid activity group.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.ActivityEvents.AsNoTracking().Where(e => e.GroupId == groupId || e.Id == groupId);
        long before = long.MaxValue;
        if (cursor is not null && (!long.TryParse(cursor, out before) || before <= 0)) throw new ArgumentException("Invalid cursor.");
        var rows = await query.Where(e => e.Id < before).OrderByDescending(e => e.Id).Take(101).ToListAsync(ct);
        return new(rows.Take(100).Select(e => new ActivityCheckDto(e.Id, Utc(e.OccurredAt), e.Kind,
            e.ReasonCode, e.BatterySoc, e.SolarWatts, e.State, e.ConfigurationVersion, e.Generation)).ToArray(),
            rows.Count > 100 ? rows[99].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    public async Task<DeviceHistoryDto> DeviceHistoryAsync(Guid id, int hours = 24, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        if (hours is < 1 or > 24) throw new ArgumentException("Choose a device history of up to 24 hours.");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.IntegrationDeviceBindings.AnyAsync(b => b.Id == id && b.Kind == "socket", ct))
            throw new ArgumentException("Choose a device from this installation.");
        var end = clock.GetUtcNow();
        var start = end.AddHours(-hours);
        var device = id.ToString("D");
        var evidence = await db.ActivityEvents.AsNoTracking().Where(e => e.Kind == "device.observed" && e.DeviceId == device
            && e.OccurredAt >= start.UtcDateTime.AddMinutes(-10) && e.OccurredAt <= end.UtcDateTime)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToListAsync(ct);
        return BuildDeviceHistory(evidence, start, end);
    }

    public static DeviceHistoryDto BuildDeviceHistory(IEnumerable<ActivityEvent> events, DateTimeOffset start, DateTimeOffset end)
    {
        var ordered = events.Where(e => Utc(e.OccurredAt) < end && Utc(e.OccurredAt).AddMinutes(10) > start)
            .GroupBy(e => e.OccurredAt).Select(g => g.OrderBy(e => e.Id).Last())
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToArray();
        var intervals = new List<DeviceIntervalDto>();
        var cursor = start;
        for (var i = 0; i < ordered.Length; i++)
        {
            var row = ordered[i];
            var from = Utc(row.OccurredAt) > start ? Utc(row.OccurredAt) : start;
            var through = Utc(row.OccurredAt).AddMinutes(10);
            if (i + 1 < ordered.Length && Utc(ordered[i + 1].OccurredAt) < through)
                through = Utc(ordered[i + 1].OccurredAt);
            if (through > end) through = end;
            if (from > cursor) { intervals.Add(new(cursor, from, null, "observation_gap")); cursor = from; }
            if (from >= end || through <= from) continue;
            intervals.Add(new(from, through, row.State, row.State.HasValue ? "provider_observation" : "state_unavailable"));
            cursor = through;
        }
        if (cursor < end) intervals.Add(new(cursor, end, null, "observation_gap"));
        var known = intervals.Where(i => i.IsOn.HasValue).Sum(i => (i.To - i.From).TotalSeconds);
        var on = intervals.Where(i => i.IsOn == true).Sum(i => (i.To - i.From).TotalSeconds);
        return new(start, end, intervals, known > 0 ? on : null, known, known < (end - start).TotalSeconds);
    }

    public async Task<RuleEvaluationDto?> EvaluationAsync(int id, CancellationToken ct = default)
    {
        await security.EnsureAsync(InstallationPermission.Read, ct);
        var rule = await rules.GetByIdAsync(id, ct);
        if (rule is null) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var latest = await db.ActivityEvents.AsNoTracking().Where(e => e.RuleId == id && e.ValuesJson != null
            && (e.Kind == "rule.checked" || e.Kind == "rule.switched" || e.Kind == "rule.failed"))
            .OrderByDescending(e => e.Id).FirstOrDefaultAsync(ct);
        RuleDecision? decision = latest?.ValuesJson is { } json ? JsonSerializer.Deserialize<RuleDecision>(json) : null;
        var same = latest?.ConfigurationVersion == RuleConfigurationVersion.Read(rule);
        var conditions = decision is null ? [] : RuleEvaluationPresentation.Conditions(rule, decision);
        var at = latest is null ? (DateTimeOffset?)null : Utc(latest.OccurredAt);
        var source = (await IntegrationSocketAssociation.ResolveSourcesAsync(db, [rule], ct))[rule.Id];
        source ??= await (from binding in db.IntegrationDeviceBindings
            join instance in db.IntegrationInstances on binding.InstanceId equals instance.Id
            where binding.Kind == "inverter" && binding.Enabled && binding.IsDefault && instance.State == "enabled"
            select (Guid?)binding.Id).SingleOrDefaultAsync(ct);
        var sourceGeneration = source is null ? (long?)null : await (from binding in db.IntegrationDeviceBindings
            join instance in db.IntegrationInstances on binding.InstanceId equals instance.Id
            where binding.Id == source && binding.Enabled && instance.State == "enabled"
            select (long?)instance.Generation).SingleOrDefaultAsync(ct);
        var sourceChanged = latest?.Generation is {} generation && (sourceGeneration is null || sourceGeneration != generation);
        return new(id, latest?.ConfigurationVersion, at, at?.AddSeconds(rule.IntervalSeconds), conditions,
            decision?.Reason.ToString() ?? "not_checked", rule.PauseReason == "manual_override" ? "paused" : rule.Enabled ? "enabled" : "disabled",
            at is null ? "unknown" : !same ? "configuration_changed" : sourceChanged ? "source_changed"
                : clock.GetUtcNow() - at > TimeSpan.FromSeconds(Math.Max(120, rule.IntervalSeconds * 2)) ? "stale" : "current",
            source);
    }

    private sealed record QueryCursor(DateTime From, DateTime Through, long Snapshot, string Filter, DateTime? Before = null, long BeforeId = 0);
    private async Task<QueryCursor> WindowAsync(DeyeSolarDbContext db, DateTimeOffset? from, DateTimeOffset? to,
        int defaultHours, int maximumHours, string? cursor, string filter, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        QueryCursor page;
        if (cursor is not null)
        {
            try { page = JsonSerializer.Deserialize<QueryCursor>(Convert.FromBase64String(cursor)) ?? throw new ArgumentException(); }
            catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { throw new ArgumentException("Invalid cursor."); }
            if (page.Filter != filter || from.HasValue && from.Value.UtcDateTime != page.From
                || to.HasValue && to.Value.UtcDateTime != page.Through) throw new ArgumentException("Cursor filters changed.");
        }
        else
        {
            var end = to ?? now;
            var start = from ?? end.AddHours(-defaultHours);
            var snapshot = filter.StartsWith("readings/")
                ? await db.Readings.Select(r => (long?)r.Id).MaxAsync(ct) ?? 0
                : await db.ActivityEvents.Select(e => (long?)e.Id).MaxAsync(ct) ?? 0;
            page = new(start.UtcDateTime, end.UtcDateTime, snapshot, filter);
        }
        if (page.Through <= page.From || page.Through - page.From > TimeSpan.FromHours(maximumHours)
            || page.Through > now.UtcDateTime.AddSeconds(1) || page.From < now.UtcDateTime.AddDays(-32)
            || page.Snapshot < 0 || page.BeforeId < 0) throw new ArgumentException("Choose a valid bounded history range.");
        return page;
    }
    private static string Encode(QueryCursor page) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(page)));
    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));
}
