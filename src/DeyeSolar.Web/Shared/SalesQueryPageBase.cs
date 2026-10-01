using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Shared;

public abstract class SalesQueryPageBase : ComponentBase
{
    [Inject] protected TimeProvider Clock { get; set; } = null!;
    [Inject] protected IOptionsMonitor<SolarSalesOptions> SalesOptions { get; set; } = null!;
    [Parameter, SupplyParameterFromQuery(Name = "period")] public string? Period { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "date")] public string? Date { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "from")] public string? From { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "through")] public string? Through { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "returnTo")] public string? ReturnTo { get; set; }
    protected ExportSalesRequest Request { get; private set; } = new(ExportSalesPeriod.Day, default);
    protected bool ValidSelection { get; private set; }
    protected string BackUrl => EnergyDetailsNavigation.ReturnPath(ReturnTo) is "/sales" && ValidSelection
        ? EnergyDetailsNavigation.SalesUrl("/sales", Request) : EnergyDetailsNavigation.ReturnPath(ReturnTo);

    protected override void OnParametersSet()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(SalesOptions.CurrentValue.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), zone).DateTime);
        ValidSelection = EnergyDetailsNavigation.TrySalesRequest(Period, Date, From, Through, today, out var request);
        Request = request;
    }
}
