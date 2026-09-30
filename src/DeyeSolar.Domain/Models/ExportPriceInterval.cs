namespace DeyeSolar.Domain.Models;

public sealed record ExportPriceInterval(DateTimeOffset Start, DateTimeOffset End, decimal PricePlnPerMwh);
