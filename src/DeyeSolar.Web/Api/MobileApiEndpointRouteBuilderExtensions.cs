using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Infrastructure.DeyeCloud;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Api;

public static class MobileApiEndpointRouteBuilderExtensions
{
    private const int MaxHistoryTake = 1000;

    public static void MapMobileApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/auth/login", async Task<IResult> (
            MobileLoginRequest request,
            MobileAuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new ApiError("Username and password are required."));
            }

            var session = await auth.SignInAsync(request);
            return session == null
                ? Results.Unauthorized()
                : Results.Ok(new MobileAuthResponse(session.Token, session.ExpiresAt, session.UserName));
        })
        .AllowAnonymous();

        var authorized = api.MapGroup("")
            .RequireAuthorization(ApiAuthorization.AuthenticatedUser);

        authorized.MapGet("/auth/session", (HttpContext context)
            => Results.Ok(new MobileSessionResponse(
                context.User.Identity?.IsAuthenticated == true,
                context.User.Identity?.Name)));

        authorized.MapPost("/auth/logout", (HttpContext context, MobileAuthService auth) =>
        {
            auth.SignOut(context.Request.Headers.Authorization);
            return Results.NoContent();
        });

        authorized.MapGet("/dashboard", ReadDashboardAsync);

        authorized.MapPost("/dashboard/refresh", async Task<IResult> (
            IInverterRefreshService refresh,
            InverterDataSnapshot inverterSnapshot,
            DeviceStatusSnapshot deviceSnapshot,
            IRuleRepository ruleRepository,
            AppSettingsService settings,
            CancellationToken ct) =>
        {
            try
            {
                await refresh.RefreshAsync(ct);
                return Results.Ok(await ReadDashboardAsync(inverterSnapshot, deviceSnapshot, ruleRepository, settings, ct));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Results.Json(new ApiError("Could not refresh Deye readings. Please try again."), statusCode: 503);
            }
        });

        authorized.MapGet("/solar/estimate", (SolarEstimateService service) => Results.Ok(service.Current));
        authorized.MapGet("/solar/history", async Task<IResult> (
            HttpContext context, ISolarHistoryService history, CancellationToken ct) =>
        {
            var query = context.Request.Query;
            var periodText = query["period"];
            var dateText = query["date"];
            DateOnly? date = null;
            if (periodText.Count != 1
                || !Enum.GetNames<SolarHistoryPeriod>().Contains(periodText.ToString(), StringComparer.Ordinal))
                return Results.BadRequest(new ApiError("Choose a valid generation period and date."));
            if (query.ContainsKey("date"))
            {
                if (dateText.Count != 1 || !DateOnly.TryParseExact(dateText, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                    return Results.BadRequest(new ApiError("Choose a valid generation period and date."));
                date = parsed;
            }
            var period = Enum.Parse<SolarHistoryPeriod>(periodText.ToString());
            try { return Results.Ok(await history.ReadAsync(period, ct, date)); }
            catch (ArgumentException)
            { return Results.BadRequest(new ApiError("Choose a generation date within the last 30 local calendar dates.")); }
        });

        authorized.MapGet("/devices", async Task<IResult> (
            bool? refresh,
            ISocketInventoryService inventory,
            DeviceStatusSnapshot snapshot,
            CancellationToken ct) =>
        {
            var devices = refresh.GetValueOrDefault()
                ? await inventory.RefreshDevicesAsync(ct)
                : snapshot.Current ?? await inventory.GetCachedDevicesAsync(ct);

            if (refresh.GetValueOrDefault() || snapshot.Current == null)
                snapshot.Update(devices);

            return Results.Ok(new DeviceListResponse(
                devices.Select(d => d.ToDto()).ToList(),
                snapshot.LastUpdated));
        });

        authorized.MapPost("/devices/state", async Task<IResult> (
            SocketStateRequest request,
            MobileSocketCommandService socketCommands,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await socketCommands.SetStateAsync(request.EntityId, request.IsOn, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        });

        authorized.MapGet("/rules", async (IRuleRepository rules, CancellationToken ct) =>
            Results.Ok((await rules.GetAllAsync(ct)).Select(r => r.ToDto()).ToList()));

        authorized.MapGet("/rules/{id:int}", async Task<IResult> (
            int id,
            IRuleRepository rules,
            CancellationToken ct) =>
        {
            var rule = await rules.GetByIdAsync(id, ct);
            return rule == null
                ? Results.NotFound(new ApiError("Rule not found."))
                : Results.Ok(rule.ToDto());
        });

        authorized.MapPost("/rules", async Task<IResult> (
            TriggerRuleRequest request,
            IRuleRepository rules,
            CancellationToken ct) =>
        {
            var result = TryBuildRule(request, existing: null, out var rule);
            if (result != null)
                return result;

            var created = await rules.CreateAsync(rule!, ct);
            return Results.Created($"/api/rules/{created.Id}", created.ToDto());
        });

        authorized.MapPut("/rules/{id:int}", async Task<IResult> (
            int id,
            TriggerRuleRequest request,
            IRuleRepository rules,
            CancellationToken ct) =>
        {
            var existing = await rules.GetByIdAsync(id, ct);
            if (existing == null)
                return Results.NotFound(new ApiError("Rule not found."));

            var result = TryBuildRule(request, existing, out var updated);
            if (result != null)
                return result;

            await rules.UpdateAsync(updated!, ct);
            return Results.Ok(updated!.ToDto());
        });

        authorized.MapPatch("/rules/{id:int}/enabled", async Task<IResult> (
            int id,
            RuleEnabledRequest request,
            IRuleRepository rules,
            CancellationToken ct) =>
        {
            var rule = await rules.GetByIdAsync(id, ct);
            if (rule == null)
                return Results.NotFound(new ApiError("Rule not found."));

            rule.Enabled = request.Enabled && !string.IsNullOrWhiteSpace(rule.EntityId);
            await rules.UpdateAsync(rule, ct);
            return Results.Ok(rule.ToDto());
        });

        authorized.MapDelete("/rules/{id:int}", async Task<IResult> (
            int id,
            IRuleRepository rules,
            CancellationToken ct) =>
        {
            await rules.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        authorized.MapGet("/readings", async (
            int? hours,
            int? take,
            IDbContextFactory<DeyeSolarDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var safeHours = Math.Clamp(hours.GetValueOrDefault(6), 1, 24 * 7);
            var safeTake = Math.Clamp(take.GetValueOrDefault(MaxHistoryTake), 1, MaxHistoryTake);
            var cutoff = DateTime.UtcNow.AddHours(-safeHours);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var readings = await db.Readings
                .Where(r => r.Timestamp >= cutoff)
                .OrderByDescending(r => r.Timestamp)
                .Take(safeTake)
                .ToListAsync(ct);

            return Results.Ok(readings.Select(r => r.ToDto()).ToList());
        });

        authorized.MapGet("/rule-runs", async (
            int? hours,
            int? take,
            string? filter,
            IDbContextFactory<DeyeSolarDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var safeHours = Math.Clamp(hours.GetValueOrDefault(6), 1, 24 * 7);
            var safeTake = Math.Clamp(take.GetValueOrDefault(MaxHistoryTake), 1, MaxHistoryTake);
            var normalizedFilter = (filter ?? "ALL").Trim().ToUpperInvariant();
            var cutoff = DateTime.UtcNow.AddHours(-safeHours);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            IQueryable<RuleRunLog> query = db.RuleRunLogs
                .Where(r => r.Timestamp >= cutoff);

            query = normalizedFilter switch
            {
                "ON" => query.Where(r => r.Action == "ON"),
                "OFF" => query.Where(r => r.Action == "OFF"),
                "CHANGES" => query.Where(r => r.Action != "NO_CHANGE"),
                _ => query
            };

            var logs = await query
                .OrderByDescending(r => r.Timestamp)
                .Take(safeTake)
                .ToListAsync(ct);

            return Results.Ok(logs.Select(r => r.ToDto()).ToList());
        });

        authorized.MapGet("/settings", async (AppSettingsService settings) =>
        {
            var deye = await settings.LoadSectionAsync<DeyeCloudOptions>(DeyeCloudOptions.Section);
            var shelly = await settings.LoadSectionAsync<ShellyOptions>(ShellyOptions.Section);
            var polling = await settings.LoadSectionAsync<PollingOptions>(PollingOptions.Section);
            var display = await settings.LoadSectionAsync<DisplayOptions>(DisplayOptions.Section);

            return Results.Ok(new MobileSettingsDto(
                deye.ToDto(),
                shelly.ToDto(),
                polling.ToDto(),
                display.ToDto()));
        });

        authorized.MapPut("/settings/deye", async (
            DeyeCloudSettingsDto request,
            AppSettingsService settings,
            DeyeCloudClient deyeClient) =>
        {
            await settings.SaveSectionAsync(DeyeCloudOptions.Section, request.ToOptions());
            deyeClient.InvalidateToken();
            return Results.NoContent();
        });

        authorized.MapGet("/settings/deye/stations", async (
            DeyeCloudClient deyeClient,
            CancellationToken ct) =>
            Results.Ok((await deyeClient.GetStationsWithDevicesAsync(ct)).Select(s => s.ToDto()).ToList()));

        authorized.MapGet("/settings/deye/stations/{stationId:long}/devices", async (
            long stationId,
            DeyeCloudClient deyeClient,
            CancellationToken ct) =>
            Results.Ok((await deyeClient.GetDevicesForStationAsync(stationId, ct)).Select(d => d.ToDto()).ToList()));

        authorized.MapPost("/settings/deye/selected-device", async Task<IResult> (
            DeyeDeviceSelectionRequest request,
            AppSettingsService settings,
            DeyeCloudClient deyeClient) =>
        {
            if (request.StationId <= 0 || string.IsNullOrWhiteSpace(request.SerialNumber))
                return Results.BadRequest(new ApiError("StationId and SerialNumber are required."));

            var deye = await settings.LoadSectionAsync<DeyeCloudOptions>(DeyeCloudOptions.Section);
            deye.StationId = request.StationId;
            deye.DeviceSn = request.SerialNumber.Trim();
            await settings.SaveSectionAsync(DeyeCloudOptions.Section, deye);
            deyeClient.InvalidateToken();
            return Results.Ok(deye.ToDto());
        });

        authorized.MapPut("/settings/shelly", async Task<IResult> (
            ShellySettingsDto request,
            AppSettingsService settings) =>
        {
            if (request.RequestIntervalMilliseconds is < 100 or > 60000)
                return Results.BadRequest(new ApiError("Shelly request interval must be between 100 and 60000 milliseconds."));

            await settings.SaveSectionAsync(ShellyOptions.Section, request.ToOptions());
            return Results.NoContent();
        });

        authorized.MapPut("/settings/polling", async Task<IResult> (
            PollingSettingsDto request,
            AppSettingsService settings) =>
        {
            if (request.IntervalSeconds is < 5 or > 300)
                return Results.BadRequest(new ApiError("Polling interval must be between 5 and 300 seconds."));

            await settings.SaveSectionAsync(PollingOptions.Section, request.ToOptions());
            return Results.NoContent();
        });

        authorized.MapPut("/settings/display", async Task<IResult> (
            DisplaySettingsDto request,
            AppSettingsService settings) =>
        {
            if (string.IsNullOrWhiteSpace(request.TimeZoneId))
                return Results.BadRequest(new ApiError("TimeZoneId is required."));

            var timeZoneId = request.TimeZoneId.Trim();
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
                return Results.BadRequest(new ApiError($"Unknown timezone '{request.TimeZoneId}'."));

            await settings.SaveSectionAsync(DisplayOptions.Section, new DisplayOptions { TimeZoneId = timeZoneId });
            return Results.NoContent();
        });

        authorized.MapPost("/settings/socket/selected-device", async Task<IResult> (
            SocketDeviceSelectionRequest request,
            AppSettingsService settings) =>
        {
            if (string.IsNullOrWhiteSpace(request.EntityId))
                return Results.BadRequest(new ApiError("EntityId is required."));

            var shelly = await settings.LoadSectionAsync<ShellyOptions>(ShellyOptions.Section);

            shelly.DeviceId = SocketEntityIds.RawIdOrSelf(request.EntityId.Trim());
            await settings.SaveSectionAsync(ShellyOptions.Section, shelly);

            return Results.Ok(new MobileSettingsDto(
                (await settings.LoadSectionAsync<DeyeCloudOptions>(DeyeCloudOptions.Section)).ToDto(),
                shelly.ToDto(),
                (await settings.LoadSectionAsync<PollingOptions>(PollingOptions.Section)).ToDto(),
                (await settings.LoadSectionAsync<DisplayOptions>(DisplayOptions.Section)).ToDto()));
        });
    }

    private static async Task<MobileDashboardResponse> ReadDashboardAsync(
        InverterDataSnapshot inverterSnapshot, DeviceStatusSnapshot deviceSnapshot,
        IRuleRepository ruleRepository, AppSettingsService settings, CancellationToken ct)
    {
        var display = await settings.LoadSectionAsync<DisplayOptions>(DisplayOptions.Section);
        var shelly = await settings.LoadSectionAsync<ShellyOptions>(ShellyOptions.Section);
        var rules = await ruleRepository.GetAllAsync(ct);
        var devices = deviceSnapshot.Current ?? Array.Empty<DevicePowerInfo>();
        var manualDevices = ManualSocketDevices.Build(rules, shelly.DeviceId, deviceSnapshot.Current);
        return new(
            inverterSnapshot.Current?.ToDto(), deviceSnapshot.Current != null, deviceSnapshot.LastUpdated,
            devices.Select(d => d.ToDto()).ToList(), manualDevices.Select(d => d.ToDto()).ToList(),
            rules.Select(r => r.ToSummaryDto()).ToList(), display.TimeZoneId);
    }

    private static IResult? TryBuildRule(
        TriggerRuleRequest request,
        TriggerRule? existing,
        out TriggerRule? rule)
    {
        rule = null;

        try
        {
            rule = request.ToRule(existing);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ApiError(ex.Message));
        }

        NormalizeRule(rule);
        var validationError = ValidateRule(rule);
        return validationError == null
            ? null
            : Results.BadRequest(new ApiError(validationError));
    }

    private static void NormalizeRule(TriggerRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.EntityId))
            rule.Enabled = false;

        if (!rule.UseSeparateSocTurnOffThreshold)
            rule.SocTurnOffThreshold = rule.SocTurnOnThreshold;

        if (rule.UseSolarProductionThreshold && rule.MinAverageSolarProductionWatts <= 0)
            rule.MinAverageSolarProductionWatts = 3000;
    }

    private static string? ValidateRule(TriggerRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
            return "Rule name is required.";

        if (rule.SocTurnOnThreshold is < 0 or > 100)
            return "SOC turn ON must be between 0 and 100%.";

        if (rule.UseSeparateSocTurnOffThreshold)
        {
            if (rule.SocTurnOffThreshold is < 0 or > 100)
                return "SOC turn OFF must be between 0 and 100%.";

            if (rule.SocTurnOffThreshold > rule.SocTurnOnThreshold)
                return "SOC turn OFF cannot be higher than SOC turn ON.";
        }

        if (rule.UseSolarProductionThreshold &&
            rule.MinAverageSolarProductionWatts is < 1 or > 30000)
        {
            return "Average PV threshold must be between 1 and 30000 W.";
        }

        if (rule.CooldownMinutes is < 1 or > 240)
            return "Cooldown must be between 1 and 240 minutes.";

        if (rule.IntervalSeconds is < 10 or > 3600)
            return "Evaluation interval must be between 10 and 3600 seconds.";

        if (rule.ActiveFrom.HasValue != rule.ActiveTo.HasValue)
            return "Set both time-window values or leave both empty.";

        return null;
    }
}
