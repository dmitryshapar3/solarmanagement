using System.Globalization;
using System.Text.Json.Serialization;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Api;

public sealed record ApiError(string Message);

public sealed record MobileLoginRequest(string Username, string Password);

public sealed record MobileAuthResponse(string Token, DateTimeOffset ExpiresAt, string Username, string? InstallationId = null);

public sealed record MobileSessionResponse(bool Authenticated, string? Username);

public sealed record MobileDashboardResponse(
    InverterDataDto? Inverter,
    bool DevicesLoaded,
    DateTimeOffset? DeviceLastUpdated,
    IReadOnlyList<DeviceDto> Devices,
    IReadOnlyList<DeviceDto> ManualDevices,
    IReadOnlyList<RuleSummaryDto> Rules,
    string TimeZoneId);

public sealed record InverterDataDto(
    int BatterySoc,
    double BatteryTemperature,
    double BatteryVoltage,
    int BatteryPower,
    double BatteryCurrent,
    int SolarProduction,
    int GridConsumption,
    int LoadPower,
    DateTimeOffset Timestamp,
    string DataSource,
    DateTimeOffset? SolarObservedAt = null,
    DateTimeOffset? GridObservedAt = null,
    string? SolarDeviceSn = null,
    string? GridDeviceSn = null,
    Guid? InverterId = null,
    bool BatterySocValid = false,
    bool BatteryPowerValid = false,
    bool BatteryTemperatureValid = false,
    bool BatteryVoltageValid = false,
    bool BatteryCurrentValid = false,
    bool LoadPowerValid = false,
    bool GridPowerValid = false,
    bool SolarPowerValid = false);

public sealed record DeviceDto(
    string Id,
    string Name,
    string? Category,
    bool Online,
    bool IsOn,
    int? CurrentPowerW,
    string? CloudName = null,
    string? LocalName = null,
    bool StateKnown = false);

public sealed record DeviceListResponse(
    IReadOnlyList<DeviceDto> Devices,
    DateTimeOffset? LastUpdated);

public sealed record RuleSummaryDto(
    int Id,
    string Name,
    string EntityId,
    bool Enabled,
    bool CurrentState,
    DateTime? CurrentStateChangedAt,
    DateTime? LastEvaluated,
    int SocTurnOnThreshold,
    int SocTurnOffThreshold,
    bool UseSeparateSocTurnOffThreshold,
    bool UseSolarProductionThreshold,
    int MinAverageSolarProductionWatts,
    int CooldownMinutes,
    int IntervalSeconds,
    string? ActiveFrom,
    string? ActiveTo,
    Guid? SourceInverterId,
    string ConfigurationVersion);

public sealed record TriggerRuleDto(
    int Id,
    string Name,
    string EntityId,
    bool Enabled,
    int SocTurnOnThreshold,
    bool UseSeparateSocTurnOffThreshold,
    int SocTurnOffThreshold,
    bool UseSolarProductionThreshold,
    int MinAverageSolarProductionWatts,
    int CooldownMinutes,
    int IntervalSeconds,
    string? ActiveFrom,
    string? ActiveTo,
    bool CurrentState,
    DateTime? CurrentStateChangedAt,
    DateTime? LastEvaluated,
    Guid? SourceInverterId,
    string ConfigurationVersion);

public sealed record TriggerRuleRequest(
    string Name,
    string EntityId,
    bool Enabled,
    int SocTurnOnThreshold,
    bool UseSeparateSocTurnOffThreshold,
    int SocTurnOffThreshold,
    bool UseSolarProductionThreshold,
    int MinAverageSolarProductionWatts,
    int CooldownMinutes,
    int IntervalSeconds,
    string? ActiveFrom,
    string? ActiveTo)
{
    public string? ConfigurationVersion { get; init; }
    [JsonRequired]
    public Guid? SourceInverterId { get; init; }
}

public sealed record RuleEnabledRequest(bool Enabled, string ConfigurationVersion);

public sealed record ReadingDto(
    int Id,
    DateTime Timestamp,
    int BatterySoc,
    double BatteryTemperature,
    double BatteryVoltage,
    int BatteryPower,
    double BatteryCurrent,
    int SolarProduction,
    int GridConsumption,
    int LoadPower,
    string DataSource,
    bool BatterySocValid,
    bool BatteryPowerValid,
    bool BatteryTemperatureValid,
    bool BatteryVoltageValid,
    bool BatteryCurrentValid,
    bool LoadPowerValid,
    bool GridPowerValid,
    bool SolarPowerValid);

public sealed record RuleRunLogDto(
    int Id,
    DateTime Timestamp,
    string RuleName,
    string Action,
    string ConditionKey,
    string Reason,
    int? BatterySoc,
    int? SolarProduction,
    int? BatteryPower);

public sealed record MobileSettingsDto(
    PollingSettingsDto Polling,
    DisplaySettingsDto Display);

public sealed record PollingSettingsDto(int IntervalSeconds);

public sealed record DisplaySettingsDto(string TimeZoneId);

public static class MobileApiMappings
{
    public static InverterDataDto ToDto(this InverterData data)
        => new(
            data.BatterySoc,
            data.BatteryTemperature,
            data.BatteryVoltage,
            data.BatteryPower,
            data.BatteryCurrent,
            data.SolarProduction,
            data.GridConsumption,
            data.LoadPower,
            data.Timestamp,
            "Integration",
            data.SolarObservedAt,
            data.GridObservedAt,
            data.SolarDeviceSn,
            data.GridDeviceSn, data.InverterId, data.BatterySocValid, data.BatteryPowerValid,
            data.BatteryTemperatureValid, data.BatteryVoltageValid, data.BatteryCurrentValid, data.LoadPowerValid,
            data.GridPowerValid, data.SolarPowerValid);

    public static DeviceDto ToDto(this DevicePowerInfo device)
        => new(
            device.Id,
            device.Name,
            device.Category,
            device.Online,
            device.IsOn,
            device.CurrentPowerW, StateKnown: device.StateKnown == true);

    public static RuleSummaryDto ToSummaryDto(this TriggerRule rule)
        => new(
            rule.Id,
            rule.Name,
            rule.EntityId,
            rule.Enabled,
            rule.CurrentState,
            rule.CurrentStateChangedAt,
            rule.LastEvaluated,
            rule.SocTurnOnThreshold,
            rule.SocTurnOffThreshold,
            rule.UseSeparateSocTurnOffThreshold,
            rule.UseSolarProductionThreshold,
            rule.MinAverageSolarProductionWatts,
            rule.CooldownMinutes,
            rule.IntervalSeconds,
            FormatTime(rule.ActiveFrom),
            FormatTime(rule.ActiveTo),
            rule.SourceInverterId,
            RuleConfigurationVersion.Read(rule));

    public static TriggerRuleDto ToDto(this TriggerRule rule)
        => new(
            rule.Id,
            rule.Name,
            rule.EntityId,
            rule.Enabled,
            rule.SocTurnOnThreshold,
            rule.UseSeparateSocTurnOffThreshold,
            rule.SocTurnOffThreshold,
            rule.UseSolarProductionThreshold,
            rule.MinAverageSolarProductionWatts,
            rule.CooldownMinutes,
            rule.IntervalSeconds,
            FormatTime(rule.ActiveFrom),
            FormatTime(rule.ActiveTo),
            rule.CurrentState,
            rule.CurrentStateChangedAt,
            rule.LastEvaluated,
            rule.SourceInverterId,
            RuleConfigurationVersion.Read(rule));

    public static ReadingDto ToDto(this Reading reading)
        => new(
            reading.Id,
            reading.Timestamp,
            reading.BatterySoc,
            reading.BatteryTemperature,
            reading.BatteryVoltage,
            reading.BatteryPower,
            reading.BatteryCurrent,
            reading.SolarProduction,
            reading.GridConsumption,
            reading.LoadPower,
            reading.DataSource,
            reading.BatterySocValid, reading.BatteryPowerValid, reading.BatteryTemperatureValid, reading.BatteryVoltageValid,
            reading.BatteryCurrentValid, reading.LoadPowerValid, reading.GridPowerValid, reading.SolarPowerValid);

    public static RuleRunLogDto ToDto(this RuleRunLog log)
        => new(
            log.Id,
            log.Timestamp,
            log.RuleName,
            log.Action,
            log.ConditionKey,
            log.Reason,
            log.BatterySoc,
            log.SolarProduction,
            log.BatteryPower);

    public static PollingSettingsDto ToDto(this PollingOptions options)
        => new(options.IntervalSeconds);

    public static DisplaySettingsDto ToDto(this DisplayOptions options)
        => new(options.TimeZoneId);

    public static PollingOptions ToOptions(this PollingSettingsDto dto)
        => new() { IntervalSeconds = dto.IntervalSeconds };

    public static DisplayOptions ToOptions(this DisplaySettingsDto dto)
        => new() { TimeZoneId = dto.TimeZoneId };

    public static TriggerRule ToRule(this TriggerRuleRequest request, TriggerRule? existing = null)
    {
        if (request.Name is null || request.EntityId is null)
            throw new InvalidOperationException("The request body is invalid or exceeds the allowed size.");
        var rule = existing ?? new TriggerRule();
        rule.ConfigurationVersion = request.ConfigurationVersion;
        rule.Name = request.Name.Trim();
        rule.EntityId = request.EntityId.Trim();
        rule.Enabled = request.Enabled;
        rule.SourceInverterId = request.SourceInverterId;
        rule.SocTurnOnThreshold = request.SocTurnOnThreshold;
        rule.UseSeparateSocTurnOffThreshold = request.UseSeparateSocTurnOffThreshold;
        rule.SocTurnOffThreshold = request.SocTurnOffThreshold;
        rule.UseSolarProductionThreshold = request.UseSolarProductionThreshold;
        rule.MinAverageSolarProductionWatts = request.MinAverageSolarProductionWatts;
        rule.CooldownMinutes = request.CooldownMinutes;
        rule.IntervalSeconds = request.IntervalSeconds;
        rule.ActiveFrom = ParseOptionalTime(request.ActiveFrom);
        rule.ActiveTo = ParseOptionalTime(request.ActiveTo);
        return rule;
    }

    private static string? FormatTime(TimeOnly? value)
        => value?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static TimeOnly? ParseOptionalTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var exact))
            return exact;

        if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        throw new InvalidOperationException($"Invalid time value '{value}'. Use HH:mm format.");
    }
}
