namespace DeyeSolar.Domain.Models;

public record DevicePowerInfo(
    string Id,
    string Name,
    string? Category,
    bool Online,
    bool IsOn,
    int? CurrentPowerW);
