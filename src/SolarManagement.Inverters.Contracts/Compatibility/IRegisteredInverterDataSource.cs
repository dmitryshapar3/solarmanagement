using DeyeSolar.Domain.Models;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Domain.Interfaces;

public interface IRegisteredInverterDataSource
{
    Task<InverterData> ReadDeviceAsync(InverterId deviceId, CancellationToken ct);
    Task<bool> IsCurrentAsync(InverterData data, CancellationToken ct);
}
