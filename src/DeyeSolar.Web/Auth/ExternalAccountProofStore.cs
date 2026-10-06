using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace DeyeSolar.Web.Auth;

// Restart invalidates pending proofs. No provider token or session token is retained here.
public sealed class ExternalAccountProofStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, ExternalProofFlow> _flows = new();
    private readonly ConcurrentDictionary<string, CompletedProof> _proofs = new();
    public static readonly string[] Operations = ["account", "identity-unlink", "contact-change", "sessions", "password", "export", "delete", "revoke-all", "identity-link"];
    public ExternalProofFlow Start(ClaimsPrincipal actor, IdentityUser user, string provider, string operation)
    {
        if (provider is not ("Google" or "Apple") || !Operations.Contains(operation, StringComparer.Ordinal))
            throw new AccountSecurityException("invalid_operation", "Choose a supported account action.", 400);
        Cleanup();
        if (_flows.Count >= 1000) throw new VerificationRateLimitException();
        var session = actor.FindFirstValue(InstallationAccessAuthorizer.SessionClaim) ?? throw new AccountSecurityException("session_invalid", "Sign in again.", 401);
        var flow = new ExternalProofFlow(RandomId(), user.Id, Hash(session), user.SecurityStamp ?? "", provider, operation,
            clock.GetUtcNow().AddMinutes(5), RandomId());
        _flows[flow.Id] = flow;
        return flow;
    }
    public ExternalProofFlow? Find(string id) => id is { Length: <= 128 } && _flows.TryGetValue(id, out var flow) && flow.ExpiresAt > clock.GetUtcNow() ? flow : null;
    public string Complete(string id, string provider, string userId)
    {
        var flow = Find(id);
        if (flow is null || flow.Provider != provider || flow.UserId != userId || !_flows.TryRemove(new(id, flow)))
            throw new AccountSecurityException("proof_failed", "Account confirmation expired. Start again.", 401);
        var proofId = RandomId();
        _proofs[proofId] = new(flow, clock.GetUtcNow().AddMinutes(2));
        return proofId;
    }
    public bool Consume(string id, ClaimsPrincipal actor, IdentityUser user, string operation)
    {
        if (id is not { Length: <= 128 } || !_proofs.TryGetValue(id, out var proof)) return false;
        var token = actor.FindFirstValue(InstallationAccessAuthorizer.SessionClaim);
        return token is not null && proof.ExpiresAt > clock.GetUtcNow() && proof.Flow.UserId == user.Id
            && proof.Flow.Stamp == user.SecurityStamp && proof.Flow.SessionHash == Hash(token) && proof.Flow.Operation == operation
            && _proofs.TryRemove(new(id, proof));
    }
    public bool Owns(ExternalProofFlow flow, ClaimsPrincipal actor, IdentityUser user) => flow.UserId == user.Id
        && flow.Stamp == user.SecurityStamp && actor.FindFirstValue(InstallationAccessAuthorizer.SessionClaim) is { } token && flow.SessionHash == Hash(token);
    public bool OwnsProof(string proofId, ClaimsPrincipal actor, IdentityUser user) => _proofs.TryGetValue(proofId, out var proof)
        && proof.ExpiresAt > clock.GetUtcNow() && Owns(proof.Flow, actor, user);
    private void Cleanup()
    {
        var now = clock.GetUtcNow();
        foreach (var item in _flows.Where(x => x.Value.ExpiresAt <= now)) _flows.TryRemove(item);
        foreach (var item in _proofs.Where(x => x.Value.ExpiresAt <= now)) _proofs.TryRemove(item);
    }
    internal static string RandomId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record CompletedProof(ExternalProofFlow Flow, DateTimeOffset ExpiresAt);
}
public sealed record ExternalProofFlow(string Id, string UserId, string SessionHash, string Stamp, string Provider,
    string Operation, DateTimeOffset ExpiresAt, string RawNonce);
public sealed class ExternalProofCompletionStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (string ProofId, DateTimeOffset Expires)> _completed = new();
    public void Add(string flowId, string proofId)
    {
        foreach (var item in _completed.Where(x => x.Value.Expires <= clock.GetUtcNow())) _completed.TryRemove(item);
        if (_completed.Count >= 1000) throw new VerificationRateLimitException();
        _completed[flowId] = (proofId, clock.GetUtcNow().AddMinutes(2));
    }
    public string? Take(string flowId, ClaimsPrincipal actor, IdentityUser user, ExternalAccountProofStore proofs)
    {
        if (flowId is not { Length: <= 128 } || !_completed.TryGetValue(flowId, out var item) || item.Expires <= clock.GetUtcNow() || !proofs.OwnsProof(item.ProofId, actor, user)) return null;
        return _completed.TryRemove(new(flowId, item)) ? item.ProofId : null;
    }
}
