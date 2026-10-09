using System.Globalization;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Data;
using DeyeSolar.Domain.Interfaces;
using Microsoft.Extensions.Options;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Services;

public sealed record SolarSiteSettings(double Latitude, double Longitude, string LocationLabel, string TimeZoneId,
    double Roof1Kwp, double Roof2Kwp, double Roof1Tilt, double Roof2Tilt, double Roof1Azimuth, double Roof2Azimuth,
    bool DeyeSolarPowerIsPvDcConfirmed = false, string DeyeSolarPowerConfirmedDeviceSn = "");
public sealed record SalesSiteSettings(string ContractStartDate, string TimeZoneId, bool PayNegativePrices,
    string? PriceSource = null, decimal? ManualPricePlnPerKwh = null, string? PriceFeedUrl = null)
{
    public SalesSiteSettings PreservePricing(SalesSiteSettings current) => this with
    { PriceSource = PriceSource ?? current.PriceSource ?? "pse", ManualPricePlnPerKwh = ManualPricePlnPerKwh ?? current.ManualPricePlnPerKwh ?? 0,
        PriceFeedUrl = PriceFeedUrl ?? current.PriceFeedUrl ?? "" };
    public static SalesSiteSettings From(SolarSalesOptions sales) => new(sales.ContractStartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        sales.TimeZoneId, sales.PayNegativePrices, sales.PriceSource, sales.ManualPricePlnPerKwh, sales.PriceFeedUrl);
}
public sealed record SiteSettingsDto(SolarSiteSettings SolarEstimate, SalesSiteSettings SolarSales, string SelectedDeviceSn = "");

public sealed class SiteSettingsService(IAppSettingsReader settings, IAppSettingsWriter writer, IOptionsMonitor<InverterConnectionOptions> inverter,
    IInverterDataSource source)
{
    private async Task<string> SelectedDeviceAsync()
    {
        if (source is IInverterSelectionRefresher refresher) await refresher.RefreshSelectionAsync(CancellationToken.None);
        return inverter.CurrentValue.DeviceKey;
    }
    public async Task<SiteSettingsDto> LoadAsync()
    {
        var solar = await settings.LoadSectionAsync<SolarEstimateOptions>(SolarEstimateOptions.Section);
        var sales = await settings.LoadSectionAsync<SolarSalesOptions>(SolarSalesOptions.Section);
        var selectedSn = await SelectedDeviceAsync();
        var confirmed = solar.DeyeSolarPowerIsPvDcConfirmed && selectedSn.Length > 0
            && solar.DeyeConfirmedDeviceSn == selectedSn;
        return new(new(solar.Latitude, solar.Longitude, solar.LocationLabel, solar.TimeZoneId,
            solar.Roof1Kwp, solar.Roof2Kwp, solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth,
            confirmed, confirmed ? selectedSn : ""),
            SalesSiteSettings.From(sales), selectedSn);
    }
    public static bool TryValidate(SiteSettingsDto? draft, out string message)
    {
        message = "Enter a valid location, solar capacity, roof orientation and time zone.";
        if (draft?.SolarEstimate is not { } solar || draft.SolarSales is not { } sales
            || solar.LocationLabel is null || solar.LocationLabel.Length > 120 || solar.LocationLabel.Any(char.IsControl)
            || solar.DeyeSolarPowerConfirmedDeviceSn is null || solar.DeyeSolarPowerConfirmedDeviceSn.Length > 128
            || solar.DeyeSolarPowerConfirmedDeviceSn.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(solar.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(solar.TimeZoneId, out _)
            || string.IsNullOrWhiteSpace(sales.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(sales.TimeZoneId, out _)) return false;
        var geometry = new SolarSiteGeometry(solar.Latitude, solar.Longitude, solar.Roof1Kwp, solar.Roof2Kwp,
            solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth);
        if (!geometry.IsValid || geometry.TotalKwp > 10000) return false;
        if (!DateOnly.TryParseExact(sales.ContractStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) || date.Year < 2000)
        { message = "Enter a contract start date from 2000 onwards as YYYY-MM-DD."; return false; }
        try { new SolarSalesOptions { ContractStartDate = date, TimeZoneId = sales.TimeZoneId, PayNegativePrices = sales.PayNegativePrices,
            PriceSource = sales.PriceSource ?? "pse", ManualPricePlnPerKwh = sales.ManualPricePlnPerKwh ?? 0, PriceFeedUrl = sales.PriceFeedUrl ?? "" }.Validate(); }
        catch (ArgumentException ex) { message = ex.Message; return false; }
        message = "";
        return true;
    }
    public async Task SaveAsync(SiteSettingsDto draft)
    {
        if (draft?.SolarSales is not { } suppliedSales || draft.SolarEstimate is null)
            throw new ArgumentException("Enter a valid location, solar capacity, roof orientation and time zone.");
        var saved = await settings.LoadSectionAsync<SolarSalesOptions>(SolarSalesOptions.Section);
        draft = draft with { SolarSales = draft.SolarSales.PreservePricing(SalesSiteSettings.From(saved)) };
        if (!TryValidate(draft, out var error)) throw new ArgumentException(error);
        var selectedSn = await SelectedDeviceAsync();
        var solar = draft.SolarEstimate;
        if (solar.DeyeSolarPowerIsPvDcConfirmed && (selectedSn.Length == 0 || solar.DeyeSolarPowerConfirmedDeviceSn != selectedSn))
            throw new ArgumentException("Save and select the primary inverter in Integrations, then reload before confirming its PV readings.");
        var salesPatch = new Dictionary<string, object?>
        { [nameof(SolarSalesOptions.ContractStartDate)] = suppliedSales.ContractStartDate, [nameof(SolarSalesOptions.TimeZoneId)] = suppliedSales.TimeZoneId,
            [nameof(SolarSalesOptions.PayNegativePrices)] = suppliedSales.PayNegativePrices };
        if (suppliedSales.PriceSource is not null) salesPatch[nameof(SolarSalesOptions.PriceSource)] = suppliedSales.PriceSource;
        if (suppliedSales.ManualPricePlnPerKwh.HasValue) salesPatch[nameof(SolarSalesOptions.ManualPricePlnPerKwh)] = suppliedSales.ManualPricePlnPerKwh.Value;
        if (suppliedSales.PriceFeedUrl is not null) salesPatch[nameof(SolarSalesOptions.PriceFeedUrl)] = suppliedSales.PriceFeedUrl;
        // Save only supplied editable properties; omitted pricing, model assumptions and server keys remain in place.
        await writer.SaveSectionsAsync(new Dictionary<string, object>
        {
            [SolarEstimateOptions.Section] = new
        {
            solar.Latitude, solar.Longitude, LocationLabel = solar.LocationLabel.Trim(), solar.TimeZoneId,
            solar.Roof1Kwp, solar.Roof2Kwp, solar.Roof1Tilt, solar.Roof2Tilt, solar.Roof1Azimuth, solar.Roof2Azimuth,
            solar.DeyeSolarPowerIsPvDcConfirmed, DeyeConfirmedDeviceSn = solar.DeyeSolarPowerIsPvDcConfirmed ? selectedSn : ""
            },
            [SolarSalesOptions.Section] = new AppSettingsPatch(salesPatch)
        });
    }
}
