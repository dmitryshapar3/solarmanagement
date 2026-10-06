using System.Globalization;
namespace DeyeSolar.Web.Components.Ui;

public static class UiInputParser
{
    public static bool TryNumber(string? value, CultureInfo culture, out double number) =>
        double.TryParse(value, NumberStyles.Float, culture, out number) && double.IsFinite(number);
    public static bool TryTime(string? value, out TimeSpan? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var parsed) || parsed >= TimeSpan.FromDays(1)) return false;
        time = parsed; return true;
    }
}
