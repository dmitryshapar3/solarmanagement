using DeyeSolar.Domain.Models;

namespace DeyeSolar.Domain.Interfaces;

public interface IInverterDataSource
{
    Task<InverterData> ReadCurrentDataAsync(CancellationToken ct);
}
