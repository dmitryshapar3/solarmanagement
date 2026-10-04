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
        return returnTo is null ? url : url + "&returnTo=" + Uri.EscapeDataString(ReturnPath(returnTo));
    }

    public static bool TrySalesRequest(string? period, string? date, string? from, string? through,
        DateOnly today, out ExportSalesRequest request)
    {
        request = new(ExportSalesPeriod.Day, today);
        if (period is not null && (!Enum.TryParse<ExportSalesPeriod>(period, true, out var parsed) || !Enum.IsDefined(parsed))) return false;
        var selectedPeriod = period is null ? ExportSalesPeriod.Day : Enum.Parse<ExportSalesPeriod>(period, true);
        var selectedDate = today;
        if (date is not null && !TryDate(date, out selectedDate)) return false;
        if (selectedDate.Year < 2000 || selectedDate > today) return false;
        if (selectedPeriod != ExportSalesPeriod.Custom) { request = new(selectedPeriod, selectedDate); return true; }
        if (!TryDate(from, out var start) || !TryDate(through, out var end)
            || start.Year < 2000 || end < start || end > today || end.DayNumber - start.DayNumber > 365) return false;
        request = new(selectedPeriod, selectedDate, start, end);
        return true;
    }

    private static bool TryDate(string? value, out DateOnly date) => DateOnly.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
