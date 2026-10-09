using System.Globalization;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Services;

namespace DeyeSolar.Web.Shared;

public enum EnergyPeriod { Day, Week, RollingMonth, CalendarMonth, Custom }

/// <summary>The same inclusive, local-calendar window is used by both Energy tabs.</summary>
public sealed record EnergyPeriodSelection(EnergyPeriod Period, DateOnly Date, DateOnly? From = null, DateOnly? Through = null)
{
    public static readonly DateOnly MinimumDate = new(2000, 1, 1);
    public DateOnly FirstDate => Period switch
    {
        EnergyPeriod.Week => Date.AddDays(-6),
        EnergyPeriod.RollingMonth => Date.AddDays(-29),
        EnergyPeriod.CalendarMonth => new(Date.Year, Date.Month, 1),
        EnergyPeriod.Custom => From ?? Date,
        _ => Date
    };
    public DateOnly LastDate => Period switch
    {
        EnergyPeriod.CalendarMonth => new(Date.Year, Date.Month, DateTime.DaysInMonth(Date.Year, Date.Month)),
        EnergyPeriod.Custom => Through ?? Date,
        _ => Date
    };
    public string Query => "period=" + (Period switch
    {
        EnergyPeriod.Day => "day", EnergyPeriod.Week => "7d", EnergyPeriod.RollingMonth => "30d",
        EnergyPeriod.CalendarMonth => "month", _ => "custom"
    }) + "&date=" + Iso(Date) + (Period == EnergyPeriod.Custom ? "&from=" + Iso(FirstDate) + "&through=" + Iso(LastDate) : "");
    public string Url(string path) => path + "?" + Query;
    public SolarHistoryPeriod ProductionPeriod => Period switch
    {
        EnergyPeriod.Day => SolarHistoryPeriod.Today, EnergyPeriod.Week => SolarHistoryPeriod.Week,
        EnergyPeriod.RollingMonth => SolarHistoryPeriod.Month, EnergyPeriod.CalendarMonth => SolarHistoryPeriod.CalendarMonth,
        _ => SolarHistoryPeriod.Custom
    };
    public ProductionRequest ToProductionRequest() => new(ProductionPeriod, Date, From, Through);
    public ExportSalesRequest ToSalesRequest() => new(Period switch
    {
        EnergyPeriod.Day => ExportSalesPeriod.Day, EnergyPeriod.Week => ExportSalesPeriod.Week,
        EnergyPeriod.RollingMonth => ExportSalesPeriod.RollingMonth, EnergyPeriod.CalendarMonth => ExportSalesPeriod.Month,
        _ => ExportSalesPeriod.Custom
    }, Date, From, Through) { AllowFuture = true };
    public EnergyPeriodSelection Move(int direction) => Period switch
    {
        EnergyPeriod.CalendarMonth => this with { Date = Date.AddMonths(direction) },
        EnergyPeriod.Custom => this with { Date = Date.AddDays(direction * (LastDate.DayNumber - FirstDate.DayNumber + 1)),
            From = FirstDate.AddDays(direction * (LastDate.DayNumber - FirstDate.DayNumber + 1)),
            Through = LastDate.AddDays(direction * (LastDate.DayNumber - FirstDate.DayNumber + 1)) },
        _ => this with { Date = Date.AddDays(direction * (Period == EnergyPeriod.Week ? 7 : Period == EnergyPeriod.RollingMonth ? 30 : 1)) }
    };
    public bool IsValid(DateOnly today) => Enum.IsDefined(Period) && FirstDate >= MinimumDate
        && LastDate >= FirstDate && LastDate.DayNumber - FirstDate.DayNumber <= 365 && LastDate <= today.AddDays(366);

    public static bool TryParse(string? period, string? date, string? from, string? through, DateOnly today, out EnergyPeriodSelection selection)
    {
        selection = new(EnergyPeriod.Day, today);
        var selected = today;
        if (date is not null && !TryDate(date, out selected)) return false;
        var mode = period?.ToLowerInvariant() switch
        {
            null or "day" or "today" => EnergyPeriod.Day,
            "7d" or "week" => EnergyPeriod.Week,
            "30d" or "rollingmonth" => EnergyPeriod.RollingMonth,
            "month" or "calendarmonth" => EnergyPeriod.CalendarMonth,
            "custom" or "year" => EnergyPeriod.Custom,
            _ => (EnergyPeriod)(-1)
        };
        if (!Enum.IsDefined(mode)) return false;
        if (period?.Equals("year", StringComparison.OrdinalIgnoreCase) == true)
            selection = new(mode, selected, new(selected.Year, 1, 1), new(selected.Year, 12, 31));
        else if (mode == EnergyPeriod.Custom)
        {
            if (!TryDate(from, out var first) || !TryDate(through, out var last)) return false;
            selection = new(mode, first, first, last);
        }
        else selection = new(mode, selected);
        try { return selection.IsValid(today); }
        catch (ArgumentOutOfRangeException) { return false; }
    }
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool TryDate(string? value, out DateOnly date) => DateOnly.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
