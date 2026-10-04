using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Integrations;

public sealed class IntegrationDeviceAliasEntity : IInstallationOwned
{
    public string InstallationId { get; set; } = "";
    public string LegacyId { get; set; } = "";
    public Guid DeviceId { get; set; }
}
