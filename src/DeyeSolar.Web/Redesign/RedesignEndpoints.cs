using System.Globalization;
using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Api;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Operations;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Redesign;

public static class RedesignEndpoints
{
    public static void MapRedesignApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(ApiAuthorization.AuthenticatedUser);
        api.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException error) { return ApiProblems.Error(error.Message, 400, "validation"); }
            catch (InvalidOperationException error) when (error.Message == "Installation settings changed. Reload the chart.")
            { return ApiProblems.Error("Installation settings changed. Reload the chart.", 409, "configuration_changed"); }
        });
        api.MapGet("/solar/production", async (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] SolarProductionService service, CancellationToken ct) =>
        {
            var request = ProductionRequest(context);
            return Results.Ok(await service.ReadAsync(request.Period, request.Date, ct));
        });
        api.MapGet("/solar/production.csv", async (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] SolarProductionService service, CancellationToken ct) =>
        {
            var request = ProductionRequest(context);
            var data = await service.ReadAsync(request.Period, request.Date, ct);
            return new CsvDownload("production.csv", async (writer, token) =>
            {
                if (request.Period != SolarHistoryPeriod.Today)
                {
                    await CsvDownload.Row(writer, "date", "observed_energy_kwh", "covered_seconds", "expected_seconds", "expected_energy_kwh", "lower_energy_kwh", "upper_energy_kwh", "partial");
                    foreach (var row in data.Days)
                    {
                        token.ThrowIfCancellationRequested();
                        await CsvDownload.Row(writer, row.Date, row.ObservedEnergyKwh, row.CoveredSeconds,
                            row.ExpectedSeconds, row.ExpectedEnergyKwh, row.LowerEnergyKwh, row.UpperEnergyKwh, row.Partial);
                    }
                    return;
                }
                await CsvDownload.Row(writer, "timestamp", "actual_kw", "observed_energy_kwh", "covered_seconds", "expected_seconds", "expected_kw", "lower_kw", "upper_kw", "partial");
                foreach (var row in data.Hours)
                {
                    token.ThrowIfCancellationRequested();
                    await CsvDownload.Row(writer, row.Timestamp, row.ActualKw, row.ObservedEnergyKwh, row.CoveredSeconds,
                        row.ExpectedSeconds, row.ExpectedKw, row.LowerKw, row.UpperKw, row.Partial);
                }
            });
        });
        api.MapGet("/activity", async (DateTimeOffset? from, DateTimeOffset? to, int? ruleId,
            bool? changesOnly, string? cursor, [Microsoft.AspNetCore.Mvc.FromServices] RedesignQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ActivityAsync(from, to, ruleId, changesOnly ?? false, cursor, ct)));
        api.MapGet("/activity/{groupId:long}/checks", async (long groupId, string? cursor, [Microsoft.AspNetCore.Mvc.FromServices] RedesignQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.ChecksAsync(groupId, cursor, ct)));
        api.MapGet("/activity.csv", async (DateTimeOffset? from, DateTimeOffset? to, int? ruleId,
            bool? changesOnly, [Microsoft.AspNetCore.Mvc.FromServices] RedesignQueries queries, CancellationToken ct) =>
        {
            var first = await queries.ActivityAsync(from, to, ruleId, changesOnly ?? false, null, ct);
            return new CsvDownload("activity.csv", async (writer, token) =>
            {
                await CsvDownload.Row(writer, "id", "start", "end", "kind", "rule_id", "rule_name", "device_id", "reason", "state", "check_count", "soc_min", "soc_max", "solar_min_w", "solar_max_w", "client");
                var page = first;
                while (true)
                {
                    foreach (var row in page.Items)
                        await CsvDownload.Row(writer, row.Id, row.Start, row.End, row.Kind, row.RuleId, row.RuleName, row.DeviceId,
                            row.ReasonCode, row.State, row.CheckCount, row.SocMin, row.SocMax, row.SolarMinWatts, row.SolarMaxWatts, row.Client);
                    if (page.NextCursor is null) break;
                    page = await queries.ActivityAsync(from, to, ruleId, changesOnly ?? false, page.NextCursor, token);
                }
            });
        });
        api.MapGet("/rules/{id:int}/evaluation", async (int id, [Microsoft.AspNetCore.Mvc.FromServices] RedesignQueries queries, CancellationToken ct) =>
            await queries.EvaluationAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        api.MapGet("/v2/devices/{id}/details", async (string id, [Microsoft.AspNetCore.Mvc.FromServices] RedesignQueries queries, CancellationToken ct) =>
            await queries.DeviceAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        api.MapGet("/v2/devices/{id:guid}/history", async (Guid id, int? hours, [Microsoft.AspNetCore.Mvc.FromServices] RedesignQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.DeviceHistoryAsync(id, hours ?? 24, ct)));
        api.MapGet("/v2/integrations/status", async ([Microsoft.AspNetCore.Mvc.FromServices] RedesignQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.IntegrationsAsync(ct)));
        api.MapGet("/readings.csv", async (int? hours, string? aggregate, [Microsoft.AspNetCore.Mvc.FromServices] RedesignQueries queries, CancellationToken ct) =>
        {
            var first = await queries.ReadingsAsync(hours ?? 6, aggregate ?? "raw", null, ct);
            return new CsvDownload("readings.csv", async (writer, token) =>
            {
                await CsvDownload.Row(writer, "id", "timestamp", "inverter_id", "configuration_revision", "runtime_generation", "data_source", "soc", "battery_temperature_c", "battery_voltage_v", "battery_power_w", "battery_current_a", "solar_power_w", "grid_power_w", "load_power_w", "solar_observed_at");
                var page = first;
                while (true)
                {
                    foreach (var row in page.Items)
                        await CsvDownload.Row(writer, row.Id, row.Timestamp, row.InverterId, row.ConfigurationRevision, row.RuntimeGeneration,
                            row.DataSource, row.BatterySoc, row.BatteryTemperature, row.BatteryVoltage, row.BatteryPower,
                            row.BatteryCurrent, row.SolarProduction, row.GridConsumption, row.LoadPower, row.SolarObservedAt);
                    if (page.NextCursor is null) break;
                    page = await queries.ReadingsAsync(hours ?? 6, aggregate ?? "raw", page.NextCursor, token);
                }
            });
        });
        api.MapGet("/sales.csv", async (HttpContext context, IExportSalesService service, CancellationToken ct) =>
        {
            var query = context.Request.Query;
            if (!Enum.TryParse<ExportSalesPeriod>(query["period"], true, out var period) || !Enum.IsDefined(period))
                throw new ArgumentException("Choose a valid sales period and date.");
            var date = ParseDate(query["date"]);
            var from = query.ContainsKey("from") ? ParseDate(query["from"]) : (DateOnly?)null;
            var through = query.ContainsKey("through") ? ParseDate(query["through"]) : (DateOnly?)null;
            var data = await service.ReadDetailsAsync(new(period, date, from, through), ct);
            return new CsvDownload("sales.csv", async (writer, token) =>
            {
                await CsvDownload.Row(writer, "start", "export_kwh", "import_kwh", "credited_export_kwh", "energy_value_pln", "observed_seconds", "average_price_pln_per_kwh", "market_average_price_pln_per_kwh");
                foreach (var row in data.Hours ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    await CsvDownload.Row(writer, row.Start, row.ExportKwh, row.ImportKwh, row.CreditedExportKwh,
                        row.EnergyValuePln, row.ObservedSeconds, row.AveragePricePlnPerKwh, row.MarketAveragePricePlnPerKwh);
                }
            });
        });
        api.MapPost("/sales/prices/recheck", async (ExportSalesRequest request, IExportSalesService service, CancellationToken ct) =>
            Results.Ok(await service.RecheckPricesAsync(request, ct))).RequireRateLimiting("price-check");
        api.MapGet("/settings/installation", async ([Microsoft.AspNetCore.Mvc.FromServices] InstallationSettingsService service, CancellationToken ct) => Results.Ok(await service.LoadAsync(ct)));
        api.MapPut("/settings/installation", async (InstallationSettingsChange request, [Microsoft.AspNetCore.Mvc.FromServices] InstallationSettingsService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(request, ct)))
            .WithMetadata(new InstallationPermissionMetadata(InstallationPermission.ManageSettings));
    }

    private static (SolarHistoryPeriod Period, DateOnly? Date) ProductionRequest(HttpContext context)
    {
        var query = context.Request.Query;
        if (!Enum.TryParse<SolarHistoryPeriod>(query["period"], true, out var period) || !Enum.IsDefined(period))
            throw new ArgumentException("Choose a valid generation period and date.");
        return (period, query.ContainsKey("date") ? ParseDate(query["date"]) : null);
    }
    private static DateOnly ParseDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date : throw new ArgumentException("Choose a valid date as YYYY-MM-DD.");
}

public sealed class CsvDownload(string filename, Func<StreamWriter, CancellationToken, Task> write) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "text/csv; charset=utf-8";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        context.Response.Headers.CacheControl = "no-store";
        await using var writer = new StreamWriter(context.Response.Body, new System.Text.UTF8Encoding(false), 8192, leaveOpen: true);
        await write(writer, context.RequestAborted);
        await writer.FlushAsync(context.RequestAborted);
    }
    public static Task Row(StreamWriter writer, params object?[] values) => writer.WriteLineAsync(string.Join(',', values.Select(Cell)));
    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => "", DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false", IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        if (value is string && text.Length > 0 && ("=+-@\t\r".Contains(text[0]))) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
