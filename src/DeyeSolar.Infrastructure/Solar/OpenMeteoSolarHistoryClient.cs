using System.Globalization;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;

namespace DeyeSolar.Infrastructure.Solar;

/// <summary>
/// Reconstruct historical radiation from Open-Meteo Best Match's archived weather models,
/// not from measurements on the roof or previously stored current-power estimates.
/// https://open-meteo.com/en/docs defines global_tilted_irradiance as the preceding hour's
/// mean; temperature and wind are instantaneous at its end. Data attribution: Open-Meteo,
/// CC BY 4.0. The free API permits non-commercial use and up to 92 past days.
/// </summary>
public sealed class OpenMeteoSolarHistoryClient(HttpClient httpClient) : ISolarHistoryRadiationSource
{
    private const string GtiVariable = "global_tilted_irradiance";
    private readonly OpenMeteoSolarClient _transport = new(httpClient);

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
        var now = DateTimeOffset.UtcNow;
        var completedThrough = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerHour, TimeSpan.Zero);
        if (end > completedThrough) end = completedThrough;
        if (end <= start) return Array.Empty<SolarWeatherSample>();

        var roofs = await Task.WhenAll(
            ReadRoofAsync(options, options.Roof1Tilt, options.Roof1Azimuth, start, end, ct),
            ReadRoofAsync(options, options.Roof2Tilt, options.Roof2Azimuth, start, end, ct));
        var samples = roofs[0].Keys.Where(time => roofs[1].ContainsKey(time)).OrderBy(time => time)
            .Select(time =>
            {
                var first = roofs[0][time];
                var second = roofs[1][time];
                return new SolarWeatherSample(DateTimeOffset.FromUnixTimeSeconds(time), first.Gti, second.Gti,
                    first.Temperature ?? second.Temperature, first.Wind ?? second.Wind, first.Cloud ?? second.Cloud);
            }).ToArray();
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
        using var document = await _transport.GetJsonAsync(OpenMeteoRequestUris.Forecast(options, parameters), ct);
        var root = document.RootElement;
        if (!HasUnit(root, "time", "unixtime") || !HasUnit(root, GtiVariable, "W/m²"))
            throw new InvalidDataException("Open-Meteo history returned missing or unexpected radiation units.");
        var times = ArrayFor(root, "time");
        var gti = ArrayFor(root, GtiVariable);
        if (times.ValueKind != JsonValueKind.Array || times.GetArrayLength() == 0 || gti.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Open-Meteo history returned an incomplete timeline.");
        var temperatures = HasUnit(root, "temperature_2m", "°C") ? ArrayFor(root, "temperature_2m") : default;
        var winds = HasUnit(root, "wind_speed_10m", "m/s") ? ArrayFor(root, "wind_speed_10m") : default;
        var clouds = HasUnit(root, "cloud_cover", "%") ? ArrayFor(root, "cloud_cover") : default;
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

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static bool HasUnit(JsonElement root, string variable, string expected) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hourly_units", out var units)
        && units.ValueKind == JsonValueKind.Object && units.TryGetProperty(variable, out var unit)
        && unit.ValueKind == JsonValueKind.String && unit.GetString() == expected;

    private static JsonElement ArrayFor(JsonElement root, string variable) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hourly", out var values)
        && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(variable, out var array)
        && array.ValueKind == JsonValueKind.Array ? array : default;

    private static double? OptionalNumber(JsonElement array, int index, double minimum, double maximum) =>
        array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength()
        && array[index].ValueKind == JsonValueKind.Number && array[index].TryGetDouble(out var value)
        && double.IsFinite(value) && value >= minimum && value <= maximum ? value : null;

    private sealed record RoofWeather(double Gti, double? Temperature, double? Wind, double? Cloud);
}
