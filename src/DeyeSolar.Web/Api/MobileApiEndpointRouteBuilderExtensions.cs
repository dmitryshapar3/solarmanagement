using DeyeSolar.Web.Auth;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Services;
using Microsoft.EntityFrameworkCore;

using DeyeSolar.Web.Operations;

namespace DeyeSolar.Web.Api;

public static class MobileApiEndpointRouteBuilderExtensions
{

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
                : Results.Ok(new MobileAuthResponse(session.Token, session.ExpiresAt, session.UserName, session.InstallationId));
        })
        .AllowAnonymous().RequireRateLimiting("identity-auth");

        var authorized = api.MapGroup("")
            .RequireAuthorization(ApiAuthorization.AuthenticatedUser);

        authorized.MapGet("/auth/session", (HttpContext context)
            => Results.Ok(new MobileSessionResponse(
                context.User.Identity?.IsAuthenticated == true,
                context.User.Identity?.Name)));

        authorized.MapPost("/auth/logout", async (HttpContext context, MobileAuthService auth) =>
        {
            await auth.SignOutAsync(context.Request.Headers.Authorization, context.RequestAborted);
            return Results.NoContent();
        });

        authorized.MapGet("/dashboard", ReadDashboardAsync);

        authorized.MapPost("/dashboard/refresh", async Task<IResult> (
            IInverterRefreshService refresh,
            InverterDataSnapshot inverterSnapshot,
            DeviceStatusSnapshot deviceSnapshot,
            IConfigurationRules ruleRepository,
            IAppSettingsReader settings,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            try
            {
                await refresh.RefreshAsync(ct);
                return Results.Ok(await ReadDashboardAsync(inverterSnapshot, deviceSnapshot, ruleRepository, settings, services, ct));
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
            IServiceProvider services,
            CancellationToken ct) =>
        {
            var epoch = snapshot.Epoch;
            var devices = refresh.GetValueOrDefault()
                ? await inventory.RefreshDevicesAsync(ct)
                : snapshot.Current ?? await inventory.GetCachedDevicesAsync(ct);

            if (refresh.GetValueOrDefault() || snapshot.Current == null)
                if (!snapshot.TryUpdate(devices, epoch)) return Results.Conflict(new ApiError("Device integrations changed. Refresh devices again."));

            return Results.Ok(new DeviceListResponse(
                await DescribeDevicesAsync(services, devices, ct),
                snapshot.LastUpdated));
        });

        authorized.MapGet("/rules", async (IConfigurationRules rules, CancellationToken ct) =>
            Results.Ok((await rules.GetAllAsync(ct)).Select(r => r.ToDto()).ToList()));

        authorized.MapGet("/rules/{id:int}", async Task<IResult> (
            int id,
            IConfigurationRules rules,
            CancellationToken ct) =>
        {
            var rule = await rules.GetByIdAsync(id, ct);
            return rule == null
                ? Results.NotFound(new ApiError("Rule not found."))
                : Results.Ok(rule.ToDto());
        });

        authorized.MapPost("/rules", async Task<IResult> (
            TriggerRuleRequest request,
            IConfigurationRules rules,
            CancellationToken ct) =>
        {
            var result = TryBuildRule(request, existing: null, out var rule);
            if (result != null)
                return result;

            try
            {
                var created = await rules.CreateAsync(rule!, ct);
                return Results.Created($"/api/rules/{created.Id}", created.ToDto());
            }
            catch (Exception error) when (ApiProblems.IsHandled(error, ApiProblemScope.Rules))
            { return ApiProblems.Describe(error, ApiProblemScope.Rules); }
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageRules));

        authorized.MapPut("/rules/{id:int}", async Task<IResult> (
            int id,
            TriggerRuleRequest request,
            IConfigurationRules rules,
            CancellationToken ct) =>
        {
            var existing = await rules.GetByIdAsync(id, ct);
            if (existing == null)
                return Results.NotFound(new ApiError("Rule not found."));

            var result = TryBuildRule(request, existing, out var updated);
            if (result != null)
                return result;

            try
            {
                await rules.UpdateAsync(updated!, ct);
                return Results.Ok(updated!.ToDto());
            }
            catch (Exception error) when (ApiProblems.IsHandled(error, ApiProblemScope.Rules))
            { return ApiProblems.Describe(error, ApiProblemScope.Rules); }
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageRules));

        authorized.MapPatch("/rules/{id:int}/enabled", async Task<IResult> (
            int id,
            RuleEnabledRequest request,
            IConfigurationRules rules,
            CancellationToken ct) =>
        {
            var rule = await rules.GetByIdAsync(id, ct);
            if (rule == null)
                return Results.NotFound(new ApiError("Rule not found."));

            rule.Enabled = request.Enabled && !string.IsNullOrWhiteSpace(rule.EntityId);
            rule.ConfigurationVersion = request.ConfigurationVersion;
            try
            {
                await rules.UpdateAsync(rule, ct);
                return Results.Ok(rule.ToDto());
            }
            catch (Exception error) when (ApiProblems.IsHandled(error, ApiProblemScope.Rules))
            { return ApiProblems.Describe(error, ApiProblemScope.Rules); }
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageRules));

        authorized.MapDelete("/rules/{id:int}", async Task<IResult> (
            int id,
            HttpContext context,
            IConfigurationRules rules,
            CancellationToken ct) =>
        {
            try
            {
                var values = context.Request.Headers.IfMatch;
                var header = values.Count == 1 ? values[0] : null;
                if (header is not { Length: 66 } || header[0] != '"' || header[^1] != '"')
                    throw new RuleConfigurationPreconditionRequiredException();
                await rules.DeleteAsync(id, header[1..^1], ct);
                return Results.NoContent();
            }
            catch (Exception error) when (ApiProblems.IsHandled(error, ApiProblemScope.Rules))
            { return ApiProblems.Describe(error, ApiProblemScope.Rules); }
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageRules));

        authorized.MapGet("/readings", async (int? hours, int? take,
            IDbContextFactory<DeyeSolarDbContext> dbFactory, TimeProvider clock, CancellationToken ct) =>
        {
            var range = HistoryQueryPolicy.Range(clock.GetUtcNow(), hours, take);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var readings = await HistoryQueryPolicy.Readings(db.Readings, range).ToListAsync(ct);
            return Results.Ok(readings.Select(reading => reading.ToDto()).ToList());
        });

        authorized.MapGet("/rule-runs", async (int? hours, int? take, string? filter,
            IDbContextFactory<DeyeSolarDbContext> dbFactory, TimeProvider clock, CancellationToken ct) =>
        {
            var range = HistoryQueryPolicy.Range(clock.GetUtcNow(), hours, take, filter);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var logs = await HistoryQueryPolicy.Runs(db.RuleRunLogs, range).ToListAsync(ct);
            return Results.Ok(logs.Select(log => log.ToDto()).ToList());
        });

        authorized.MapGet("/settings", async (IAppSettingsReader settings) =>
        {
            var polling = await settings.LoadSectionAsync<PollingOptions>(PollingOptions.Section);
            var display = await settings.LoadSectionAsync<DisplayOptions>(DisplayOptions.Section);

            return Results.Ok(new MobileSettingsDto(
                polling.ToDto(),
                display.ToDto()));
        });

        authorized.MapPut("/settings/polling", async Task<IResult> (
            PollingSettingsDto request,
            IAppSettingsWriter settings) =>
        {
            if (request.IntervalSeconds is < 5 or > 300)
                return Results.BadRequest(new ApiError("Polling interval must be between 5 and 300 seconds."));

            await settings.SaveSectionAsync(PollingOptions.Section, request.ToOptions());
            return Results.NoContent();
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageSettings));

        authorized.MapPut("/settings/display", async Task<IResult> (
            DisplaySettingsDto request,
            IAppSettingsWriter settings) =>
        {
            if (string.IsNullOrWhiteSpace(request.TimeZoneId))
                return Results.BadRequest(new ApiError("TimeZoneId is required."));

            var timeZoneId = request.TimeZoneId.Trim();
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
                return Results.BadRequest(new ApiError($"Unknown timezone '{request.TimeZoneId}'."));

            await settings.SaveSectionAsync(DisplayOptions.Section, new DisplayOptions { TimeZoneId = timeZoneId });
            return Results.NoContent();
        }).WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageSettings));

    }

    private static async Task<MobileDashboardResponse> ReadDashboardAsync(
        InverterDataSnapshot inverterSnapshot, DeviceStatusSnapshot deviceSnapshot,
        IConfigurationRules ruleRepository, IAppSettingsReader settings, IServiceProvider services, CancellationToken ct)
    {
        var display = await settings.LoadSectionAsync<DisplayOptions>(DisplayOptions.Section);
        var rules = await ruleRepository.GetAllAsync(ct);
        var devices = deviceSnapshot.Current ?? Array.Empty<DevicePowerInfo>();
        var manualDevices = ManualSocketDevices.Build(rules, deviceSnapshot.Current);
        return new(
            inverterSnapshot.Current?.ToDto(), deviceSnapshot.Current != null, deviceSnapshot.LastUpdated,
            await DescribeDevicesAsync(services, devices, ct), await DescribeDevicesAsync(services, manualDevices, ct),
            rules.Select(r => r.ToSummaryDto()).ToList(), display.TimeZoneId);
    }

    private static Task<IReadOnlyList<DeviceDto>> DescribeDevicesAsync(IServiceProvider services,
        IReadOnlyList<DevicePowerInfo> devices, CancellationToken ct)
        => services.GetService<DeviceNameService>() is { } names ? names.DescribeAsync(devices, ct)
            : Task.FromResult<IReadOnlyList<DeviceDto>>(devices.Select(device => device.ToDto()).ToArray());

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
            return ApiProblems.Error(ex.Message, 400, "validation");
        }

        RuleConfigurationPolicy.Normalize(rule);
        var validationError = RuleConfigurationPolicy.Validate(rule)?.Message;
        return validationError == null
            ? null
            : ApiProblems.Error(validationError, 400, "validation");
    }

}
