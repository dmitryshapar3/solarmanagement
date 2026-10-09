using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Services;

public enum ExportSalesPeriod { Day, Month, Year, Custom, Week, RollingMonth }

public sealed record ExportSalesRequest(ExportSalesPeriod Period, DateOnly Date,
    DateOnly? From = null, DateOnly? Through = null)
{
    // Shared web filters may retain upcoming dates when switching from the generation forecast.
    // This is a read policy, not part of the existing mobile request/response contract.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllowFuture { get; init; }
}

public sealed record ExportSalesResult(ExportSalesRequest Request, DateOnly Today, DateOnly ContractStartDate,
    string TimeZoneId, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<ExportSaleBucket> Buckets,
    decimal? ExportKwh, decimal? CreditedExportKwh, decimal? EnergyValuePln, decimal? EstimatedDepositPln,
    int ExpectedHours, int ObservedHours, int ValuedHours, string? DataError = null, string? PriceError = null,
    ExportSaleProgress? CurrentHour = null, DateTimeOffset? UpdatedAt = null)
{
    public bool IsPartial => ObservedHours < ExpectedHours || ValuedHours < ObservedHours;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExportSaleHour>? Hours { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DateTimeOffset>? MissingPriceHours { get; init; }
}

public interface IExportSalesService
{
    Task<ExportSalesResult> ReadAsync(ExportSalesRequest request, CancellationToken ct);
    Task<ExportSalesResult> ReadDetailsAsync(ExportSalesRequest request, CancellationToken ct) => ReadAsync(request, ct);
    Task<ExportSalesResult> RecheckPricesAsync(ExportSalesRequest request, CancellationToken ct) => ReadDetailsAsync(request, ct);
}
