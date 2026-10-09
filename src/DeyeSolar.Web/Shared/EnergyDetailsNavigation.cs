using System.Globalization;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Shared;

public static class EnergyDetailsNavigation
{
    public static string ReturnPath(string? value) => value is "/generation" or "/sales" ? value : "/";

    public static string SalesUrl(string path, ExportSalesRequest request, string? returnTo = null)
    {
        var url = path + "?period=" + request.Period + "&date=" + request.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.Period == ExportSalesPeriod.Custom)
            url += "&from=" + request.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + "&through=" + request.Through?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.AllowFuture) url += "&includeUpcoming=true";
        return returnTo is null ? url : url + "&returnTo=" + Uri.EscapeDataString(ReturnPath(returnTo));
    }

    public static bool TrySalesRequest(string? period, string? date, string? from, string? through,
        DateOnly today, out ExportSalesRequest request, bool allowFuture = false)
    {
        request = new(ExportSalesPeriod.Day, today);
        if (period is not null && (!Enum.TryParse<ExportSalesPeriod>(period, true, out var parsed) || !Enum.IsDefined(parsed))) return false;
        var selectedPeriod = period is null ? ExportSalesPeriod.Day : Enum.Parse<ExportSalesPeriod>(period, true);
        var selectedDate = today;
        if (date is not null && !TryDate(date, out selectedDate)) return false;
        var latest = allowFuture ? today.AddDays(366) : today;
        if (selectedDate.Year < 2000 || selectedDate > latest) return false;
        if (selectedPeriod != ExportSalesPeriod.Custom)
        {
            if (selectedPeriod == ExportSalesPeriod.Week && selectedDate.AddDays(-6).Year < 2000
                || selectedPeriod == ExportSalesPeriod.RollingMonth && selectedDate.AddDays(-29).Year < 2000) return false;
            request = new(selectedPeriod, selectedDate) { AllowFuture = allowFuture };
            return true;
        }
        if (!TryDate(from, out var start) || !TryDate(through, out var end)
            || start.Year < 2000 || end < start || end > latest || end.DayNumber - start.DayNumber > 365) return false;
        request = new(selectedPeriod, selectedDate, start, end) { AllowFuture = allowFuture };
        return true;
    }

    private static bool TryDate(string? value, out DateOnly date) => DateOnly.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
