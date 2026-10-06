namespace DeyeSolar.Domain.Models;

public record DevicePowerInfo(
    string Id,
    string Name,
    string? Category,
    bool Online,
    bool IsOn,
    int? CurrentPowerW,
    bool StateKnown = false)
{
    // An additive property preserves the existing constructor used by provider packages.
    public string? CloudName { get; init; }
}
