using DeyeSolar.Web.Data;
using DeyeSolar.Domain.Models;

namespace DeyeSolar.Web.Integrations;

public sealed class IntegrationOAuthFlowEntity : IInstallationOwned
{
    public Guid Id { get; set; }
    public string InstallationId { get; set; } = "";
    public Guid InstanceId { get; set; }
    public string UserId { get; set; } = "";
    public string? SecurityStamp { get; set; }
    public string StateHash { get; set; } = "";
    public string Client { get; set; } = "web";
    public string Status { get; set; } = "pending";
    public string? Code { get; set; }
    public long Revision { get; set; }
    public long Generation { get; set; }
    public string PackageVersion { get; set; } = "";
    public string PackageDigest { get; set; } = "";
    public string DescriptorDigest { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
