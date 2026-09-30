namespace DeyeSolar.Domain.Models;

public sealed record ExportSaleHour(DateTimeOffset Start, decimal? ExportKwh, decimal? ImportKwh,
    decimal? CreditedExportKwh, decimal? EnergyValuePln, int ObservedSeconds, decimal? AveragePricePlnPerKwh);

public sealed record ExportSaleProgress(DateTimeOffset Start, DateTimeOffset? ObservedThrough,
    decimal? ExportKwh, decimal? CreditedExportKwh, decimal? EnergyValuePln,
    decimal? EstimatedDepositPln, int ObservedSeconds);

public sealed record ExportSaleBucket(DateTimeOffset Start, DateTimeOffset End, decimal? ExportKwh,
    decimal? CreditedExportKwh, decimal? EnergyValuePln, decimal? EstimatedDepositPln,
    int ExpectedHours, int ObservedHours, int ValuedHours);
