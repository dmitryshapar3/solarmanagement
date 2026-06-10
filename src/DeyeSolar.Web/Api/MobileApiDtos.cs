using System.Globalization;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.DeyeCloud;
using DeyeSolar.Web.Data;

namespace DeyeSolar.Web.Api;

public sealed record ApiError(string Message);

public sealed record MobileLoginRequest(string Username, string Password);

public sealed record MobileAuthResponse(string Token, DateTimeOffset ExpiresAt, string Username);

public sealed record MobileSessionResponse(bool Authenticated, string? Username);

public sealed record MobileDashboardResponse(
    InverterDataDto? Inverter,
    bool DevicesLoaded,
    DateTimeOffset? DeviceLastUpdated,
    IReadOnlyList<DeviceDto> Devices,
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
    string DataSource);

public sealed record DeviceDto(
    string Id,
    string Name,
    string? Category,
    bool Online,
    bool IsOn,
    int? CurrentPowerW);

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
    string? ActiveTo);

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
    DateTime? LastEvaluated);

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
    string? ActiveTo);

public sealed record RuleEnabledRequest(bool Enabled);

public sealed record SocketStateRequest(string EntityId, bool IsOn);

public sealed record SocketStateResponse(string EntityId, bool IsOn, DeviceDto? Device);

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
    string DataSource);

public sealed record RuleRunLogDto(
    int Id,
    DateTime Timestamp,
    string RuleName,
    string Action,
    string ConditionKey,
    string Reason,
    int BatterySoc,
    int SolarProduction,
    int BatteryPower);

public sealed record MobileSettingsDto(
    DeyeCloudSettingsDto DeyeCloud,
    ShellySettingsDto Shelly,
    PollingSettingsDto Polling,
    DisplaySettingsDto Display);

public sealed record DeyeCloudSettingsDto(
    string BaseUrl,
    string AppId,
    string AppSecret,
    string Email,
    string Password,
    long StationId,
    string DeviceSn);

public sealed record ShellySettingsDto(
    string ServerUri,
    string AuthKey,
    string DeviceId,
    int RequestIntervalMilliseconds);

public sealed record PollingSettingsDto(int IntervalSeconds);

public sealed record DisplaySettingsDto(string TimeZoneId);

public sealed record DeyeStationDto(long Id, string Name, string? Address);

public sealed record DeyeDeviceDto(string SerialNumber, string DeviceType, long DeviceId, long StationId);

public sealed record DeyeDeviceSelectionRequest(long StationId, string SerialNumber);

public sealed record SocketDeviceSelectionRequest(string EntityId);

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
            "DeyeCloud");

    public static DeviceDto ToDto(this DevicePowerInfo device)
        => new(
            device.Id,
            device.Name,
            device.Category,
            device.Online,
            device.IsOn,
            device.CurrentPowerW);

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
            FormatTime(rule.ActiveTo));

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
            rule.LastEvaluated);

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
            reading.DataSource);

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

    public static DeyeCloudSettingsDto ToDto(this DeyeCloudOptions options)
        => new(
            options.BaseUrl,
            options.AppId,
            options.AppSecret,
            options.Email,
            options.Password,
            options.StationId,
            options.DeviceSn);

    public static ShellySettingsDto ToDto(this ShellyOptions options)
        => new(options.ServerUri, options.AuthKey, options.DeviceId, options.RequestIntervalMilliseconds);

    public static PollingSettingsDto ToDto(this PollingOptions options)
        => new(options.IntervalSeconds);

    public static DisplaySettingsDto ToDto(this DisplayOptions options)
        => new(options.TimeZoneId);

    public static DeyeStationDto ToDto(this DeyeStation station)
        => new(station.Id, station.Name, station.Address);

    public static DeyeDeviceDto ToDto(this DeyeDevice device)
        => new(device.SerialNumber, device.DeviceType, device.DeviceId, device.StationId);

    public static DeyeCloudOptions ToOptions(this DeyeCloudSettingsDto dto)
        => new()
        {
            BaseUrl = dto.BaseUrl,
            AppId = dto.AppId,
            AppSecret = dto.AppSecret,
            Email = dto.Email,
            Password = dto.Password,
            StationId = dto.StationId,
            DeviceSn = dto.DeviceSn
        };

    public static ShellyOptions ToOptions(this ShellySettingsDto dto)
        => new()
        {
            ServerUri = dto.ServerUri,
            AuthKey = dto.AuthKey,
            DeviceId = dto.DeviceId,
            RequestIntervalMilliseconds = dto.RequestIntervalMilliseconds
        };

    public static PollingOptions ToOptions(this PollingSettingsDto dto)
        => new() { IntervalSeconds = dto.IntervalSeconds };

    public static DisplayOptions ToOptions(this DisplaySettingsDto dto)
        => new() { TimeZoneId = dto.TimeZoneId };

    public static TriggerRule ToRule(this TriggerRuleRequest request, TriggerRule? existing = null)
    {
        var rule = existing ?? new TriggerRule();
        rule.Name = request.Name.Trim();
        rule.EntityId = request.EntityId.Trim();
        rule.Enabled = request.Enabled;
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
