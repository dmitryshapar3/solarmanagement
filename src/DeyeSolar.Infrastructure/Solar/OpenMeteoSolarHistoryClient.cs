using System.Text.Json;
using System.Globalization;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using static DeyeSolar.Infrastructure.Solar.OpenMeteoSeries;

namespace DeyeSolar.Infrastructure.Solar;

/// <summary>
/// Reconstruct historical radiation from Open-Meteo Best Match's archived weather models,
/// not from measurements on the roof or previously stored current-power estimates.
/// https://open-meteo.com/en/docs defines global_tilted_irradiance as the preceding hour's
/// mean; temperature and wind are instantaneous at its end. Data attribution: Open-Meteo,
/// CC BY 4.0. The free API permits non-commercial use and up to 92 past days.
/// </summary>
public sealed class OpenMeteoSolarHistoryClient(IOpenMeteoJsonReader transport, TimeProvider clock) : ISolarHistoryRadiationSource, ISolarDayForecastSource
{
    private const string GtiVariable = "global_tilted_irradiance";

    public async Task<SolarDayForecast> ReadAsync(SolarEstimateOptions options, DateTimeOffset start,
        DateTimeOffset end, DateOnly selectedDate, CancellationToken ct)
    {
        options.Validate();
        var now = clock.GetUtcNow();
        var from = new DateTimeOffset(start.UtcTicks - start.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        var through = new DateTimeOffset(end.UtcTicks - end.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        if (through < end) through = through.AddHours(1);
        if (through <= from || through - from > TimeSpan.FromDays(31) || end > now.AddDays(2))
            throw new ArgumentException("Choose a bounded production forecast range.");
        var roofs = await Task.WhenAll(
            ReadRoofAsync(options, options.Roof1Tilt, options.Roof1Azimuth, from, through, ct),
            ReadRoofAsync(options, options.Roof2Tilt, options.Roof2Azimuth, from, through, ct));
        var parameters = new Dictionary<string, string>
        {
            ["latitude"] = Number(options.Latitude), ["longitude"] = Number(options.Longitude),
            ["daily"] = "sunrise,sunset", ["timeformat"] = "unixtime", ["timezone"] = options.TimeZoneId,
            ["start_date"] = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["end_date"] = selectedDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        using var document = await transport.ReadAsync(OpenMeteoRequestUris.Forecast(options, parameters), ct);
        DateTimeOffset? ReadTime(string field, int index)
        {
            if (!HasUnit(document.RootElement, "daily", field, "unixtime")) return null;
            var values = ArrayFor(document.RootElement, "daily", field);
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() <= index
                || values[index].ValueKind != JsonValueKind.Number || !values[index].TryGetInt64(out var seconds) || seconds <= 0) return null;
            try
            {
                var value = DateTimeOffset.FromUnixTimeSeconds(seconds);
                var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime) == selectedDate.AddDays(index) ? value : null;
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return new(CombineRoofs(roofs[0], roofs[1]).ToArray(), now,
            ReadTime("sunrise", 0), ReadTime("sunset", 0), ReadTime("sunrise", 1));
    }

    public async Task<IReadOnlyList<SolarWeatherSample>> ReadAsync(SolarEstimateOptions options,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        options.Validate();
        ct.ThrowIfCancellationRequested();
        start = start.ToUniversalTime();
        end = end.ToUniversalTime();
        if (start.Ticks % TimeSpan.TicksPerHour != 0 || end.Ticks % TimeSpan.TicksPerHour != 0
            || end <= start || end - start > TimeSpan.FromDays(31))
            throw new ArgumentException("Solar history requires whole UTC hours over a positive range of at most 31 days.");

        // Capture the boundary before I/O; a forecast value for an unfinished hour is not history.
        var now = clock.GetUtcNow();
        var completedThrough = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        if (end > completedThrough) end = completedThrough;
        if (end <= start) return Array.Empty<SolarWeatherSample>();

        var roofs = await Task.WhenAll(
            ReadRoofAsync(options, options.Roof1Tilt, options.Roof1Azimuth, start, end, ct),
            ReadRoofAsync(options, options.Roof2Tilt, options.Roof2Azimuth, start, end, ct));
        var samples = CombineRoofs(roofs[0], roofs[1]).ToArray();
        if (samples.Length == 0)
            throw new InvalidDataException("Open-Meteo returned no common valid completed hours for solar history.");
        return Array.AsReadOnly(samples);
    }

    private async Task<Dictionary<long, RoofWeather>> ReadRoofAsync(SolarEstimateOptions options,
        double tilt, double compassAzimuth, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["latitude"] = Number(options.Latitude), ["longitude"] = Number(options.Longitude),
            ["tilt"] = Number(tilt), ["azimuth"] = Number(SolarPowerCalculator.ToOpenMeteoAzimuth(compassAzimuth)),
            ["models"] = OpenMeteoCurrentSolarClient.WeatherModel,
            ["hourly"] = GtiVariable + ",temperature_2m,wind_speed_10m,cloud_cover",
            ["wind_speed_unit"] = "ms", ["timeformat"] = "unixtime", ["timezone"] = "UTC",
            ["start_date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // The row at the exclusive end describes the last included hour, even at midnight.
            ["end_date"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        // Recent history uses the Forecast API's date range, preserving the existing source.
        using var document = await transport.ReadAsync(OpenMeteoRequestUris.Forecast(options, parameters), ct);
        var root = document.RootElement;
        if (!HasUnit(root, "hourly", "time", "unixtime") || !HasUnit(root, "hourly", GtiVariable, "W/m²"))
            throw new InvalidDataException("Open-Meteo history returned missing or unexpected radiation units.");
        var times = ArrayFor(root, "hourly", "time");
        var gti = ArrayFor(root, "hourly", GtiVariable);
        if (times.ValueKind != JsonValueKind.Array || times.GetArrayLength() == 0 || gti.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Open-Meteo history returned an incomplete timeline.");
        var temperatures = HasUnit(root, "hourly", "temperature_2m", "°C") ? ArrayFor(root, "hourly", "temperature_2m") : default;
        var winds = HasUnit(root, "hourly", "wind_speed_10m", "m/s") ? ArrayFor(root, "hourly", "wind_speed_10m") : default;
        var clouds = HasUnit(root, "hourly", "cloud_cover", "%") ? ArrayFor(root, "hourly", "cloud_cover") : default;
        var result = new Dictionary<long, RoofWeather>();
        long? previous = null;
        var firstHour = start.ToUnixTimeSeconds();
        var lastHourExclusive = end.ToUnixTimeSeconds();
        for (var index = 0; index < times.GetArrayLength(); index++)
        {
            var time = times[index];
            if (time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out var timestamp)
                || timestamp is < -62135593200 or > 253402300799 || timestamp % 3600 != 0
                || (previous.HasValue && timestamp <= previous.Value))
                throw new InvalidDataException("Open-Meteo history timeline is not ordered UTC hourly data.");
            previous = timestamp;
            var hourStart = timestamp - 3600;
            if (hourStart < firstHour || hourStart >= lastHourExclusive) continue;
            var irradiance = OptionalNumber(gti, index, 0, 2000);
            if (irradiance is null) continue;
            // Missing hours remain gaps. Never borrow radiation from another hour or roof.
            result[hourStart] = new(irradiance.Value, OptionalNumber(temperatures, index, -80, 65),
                OptionalNumber(winds, index, 0, 100), OptionalNumber(clouds, index, 0, 100));
        }
        return result;
    }

}
