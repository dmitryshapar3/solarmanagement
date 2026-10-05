using System.Globalization;
using System.Text.Json;
using DeyeSolar.Domain.Models;

namespace DeyeSolar.Infrastructure.Solar;

internal sealed record RoofWeather(double Gti, double? Temperature, double? Wind, double? Cloud);

/// <summary>Shared shape and scalar parsing; each source retains its distinct time and completeness rules.</summary>
internal static class OpenMeteoSeries
{
    public static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    public static bool HasUnit(JsonElement root, string series, string variable, string expected) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(series + "_units", out var units)
        && units.ValueKind == JsonValueKind.Object && units.TryGetProperty(variable, out var unit)
        && unit.ValueKind == JsonValueKind.String && unit.GetString() == expected;
    public static JsonElement ArrayFor(JsonElement root, string series, string variable) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(series, out var values)
        && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(variable, out var array)
        && array.ValueKind == JsonValueKind.Array ? array : default;
    public static double? OptionalNumber(JsonElement array, int index, double minimum, double maximum) =>
        array.ValueKind == JsonValueKind.Array && index >= 0 && index < array.GetArrayLength()
        && TryNumber(array[index], minimum, maximum, out var value) ? value : null;
    public static bool TryNumber(JsonElement value, double minimum, double maximum, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number)
            && double.IsFinite(number) && number >= minimum && number <= maximum;
    }
    public static bool TryTimestamp(JsonElement value, out long timestamp)
    {
        timestamp = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out timestamp)
            && timestamp is >= -62135596800 and <= 253402300799;
    }
    public static IEnumerable<SolarWeatherSample> CombineRoofs(IReadOnlyDictionary<long, RoofWeather> first,
        IReadOnlyDictionary<long, RoofWeather> second)
    {
        foreach (var time in first.Keys.Where(second.ContainsKey).OrderBy(time => time))
        {
            var a = first[time];
            var b = second[time];
            yield return new(DateTimeOffset.FromUnixTimeSeconds(time), a.Gti, b.Gti,
                a.Temperature ?? b.Temperature, a.Wind ?? b.Wind, a.Cloud ?? b.Cloud);
        }
    }
    // Sparse series must not imply stable conditions; nighttime zero is legitimately stable.
    public static double Variability(IEnumerable<double> values)
    {
        var samples = values.ToArray();
        if (samples.Length < 3) return 0.4;
        var mean = samples.Average();
        return mean <= 1 ? 0 : Math.Clamp((samples.Max() - samples.Min()) / mean, 0, 2);
    }
}
