namespace DeyeSolar.Domain.Models;

public sealed record ExportGridSample(DateTimeOffset Timestamp, int GridPowerWatts,
    Guid? InverterId = null, long ConfigurationRevision = 0, long RuntimeGeneration = 0);
