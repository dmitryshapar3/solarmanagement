namespace SolarManagement.Inverters.Contracts;

public interface IInverterSelectionRefresher
{
    Task RefreshSelectionAsync(CancellationToken ct);
}
