using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Services;

public enum ExportSalesPeriod { Day, Month, Year, Custom }

public sealed record ExportSalesRequest(ExportSalesPeriod Period, DateOnly Date,
    DateOnly? From = null, DateOnly? Through = null);

public sealed record ExportSalesResult(ExportSalesRequest Request, DateOnly Today, DateOnly ContractStartDate,
    string TimeZoneId, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<ExportSaleBucket> Buckets,
    decimal? ExportKwh, decimal? CreditedExportKwh, decimal? EnergyValuePln, decimal? EstimatedDepositPln,
    int ExpectedHours, int ObservedHours, int ValuedHours, string? DataError = null, string? PriceError = null,
    ExportSaleProgress? CurrentHour = null, DateTimeOffset? UpdatedAt = null)
{
    public bool IsPartial => ObservedHours < ExpectedHours || ValuedHours < ObservedHours;
}

public interface IExportSalesService
{
    Task<ExportSalesResult> ReadAsync(ExportSalesRequest request, CancellationToken ct);
}
