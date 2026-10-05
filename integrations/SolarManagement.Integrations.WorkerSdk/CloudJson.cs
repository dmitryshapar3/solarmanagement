using System.Globalization;
using System.Text.Json;

namespace SolarManagement.Integrations.WorkerSdk;

/// <summary>Primitive decoding shared across providers; coercion and timestamp precision remain explicit.</summary>
internal static class CloudJson
{
    public static JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    public static string? Text(JsonElement value, string key, bool allowNumber = false)
    {
        var field = Property(value, key);
        return field.ValueKind switch
        {
            JsonValueKind.String => field.GetString(),
            JsonValueKind.Number when allowNumber => field.GetRawText(),
            _ => null
        };
    }
    public static bool? Boolean(JsonElement value, string key) => Property(value, key).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    public static decimal? Number(JsonElement value, string key) => Decimal(Property(value, key));
    public static decimal? Decimal(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number
        : value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    public static DateTimeOffset? Timestamp(decimal? value, bool milliseconds, bool requireWhole = false)
    {
        if (value is null || value < 1 || value > (milliseconds ? 253402300799999m : 253402300799m)
            || requireWhole && value != decimal.Truncate(value.Value)) return null;
        try { return milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value) : DateTimeOffset.FromUnixTimeSeconds((long)value.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
