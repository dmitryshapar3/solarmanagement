using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Auth;

public sealed record ExternalProofStartResponse(string FlowId, string? AuthorizationUrl, string? RawNonce, DateTimeOffset ExpiresAt);
public sealed class ExternalAccountProofService(AccountFreshProofVerifier fresh, ExternalAccountProofStore proofs,
    ExternalProofCompletionStore completed, AuthProviderOptions providers, UserManager<IdentityUser> users)
{
    public async Task<ExternalProofStartResponse> StartAsync(ClaimsPrincipal actor, string provider, string operation, CancellationToken ct)
    {
        var user = await fresh.ActorAsync(actor, ct);
        provider = provider.Equals("google", StringComparison.OrdinalIgnoreCase) ? "Google" : provider.Equals("apple", StringComparison.OrdinalIgnoreCase) ? "Apple" : "";
        if (!(await users.GetLoginsAsync(user)).Any(x => x.LoginProvider == provider)) throw new AccountSecurityException("identity_not_linked", "Confirm an identity already linked to this account.", 400);
        if (provider == "Google" && !providers.GoogleEnabled || provider == "Apple" && !providers.Apple.NativeAvailable) throw new AccountSecurityException("provider_unavailable", "This sign-in provider is unavailable.", 503);
        var flow = proofs.Start(actor, user, provider, operation);
        var url = provider == "Google" || providers.Apple.WebAvailable ? providers.PublicBaseUrl.TrimEnd('/') + (provider == "Google" ? "/auth/google?proofFlow=" : "/auth/apple?proofFlow=") + Uri.EscapeDataString(flow.Id) : null;
        return new(flow.Id, url, provider == "Apple" ? flow.RawNonce : null, flow.ExpiresAt);
    }
    public async Task<string> CompleteAsync(ClaimsPrincipal actor, string flowId, CancellationToken ct)
    {
        var user = await fresh.ActorAsync(actor, ct);
        return completed.Take(flowId, actor, user, proofs) ?? throw new AccountSecurityException("proof_pending", "Finish account confirmation first.", 409);
    }
}
