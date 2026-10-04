using System.Security.Claims;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Billing;

namespace DeyeSolar.Web.Integrations;

public interface IIntegrationManagerAccess
{
    Task EnsureAsync(ClaimsPrincipal actor, string installationId, CancellationToken ct);
}

public sealed class IntegrationManagerAccess(InstallationMembershipService memberships,
    IInstallationAccessAuthorizer authorizer, IBillingAccessReader? billing = null) : IIntegrationManagerAccess
{
    public async Task EnsureAsync(ClaimsPrincipal actor, string installationId, CancellationToken ct)
    {
        var membership = await memberships.ResolveAsync(actor, ct);
        if (membership is null || membership.InstallationId != installationId || membership.Role is not ("Owner" or "IntegrationManager"))
            throw new IntegrationRequestException("forbidden", "You do not have permission to manage this installation's integrations.", 403);
        await authorizer.CheckAsync(actor, installationId, InstallationPermission.ManageIntegrations, ct);
        if (billing is not null) await billing.EnsureUserAsync(actor, ct);
    }
}
