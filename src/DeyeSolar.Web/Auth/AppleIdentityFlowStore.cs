using System.Collections.Concurrent;

namespace DeyeSolar.Web.Auth;

public sealed record AppleLinkStartRequest(AccountSecurityProof Proof);
public sealed record AppleIdentityExchangeRequest(string IdentityToken, string RawNonce, string AuthorizationCode, string? Name = null, string? Email = null);
public sealed record AppleLinkCompleteRequest(string FlowId, string IdentityToken, string RawNonce, string AuthorizationCode);
public sealed record AppleLinkFlow(string Id, string UserId, string SessionHash, string Stamp, string RawNonce, DateTimeOffset ExpiresAt);
public sealed record AppleWebFlow(string Id, string CorrelationHash, string RawNonce, string ReturnUrl, string? ExternalProofFlowId, string? LinkFlowId, DateTimeOffset ExpiresAt);
public sealed record PendingAppleWebLink(AppleLinkFlow Flow, VerifiedAppleIdentity Identity, AppleIdentityTokens Tokens, string Audience, DateTimeOffset ExpiresAt);
public sealed class AppleIdentityFlowStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, AppleLinkFlow> _links = new();
    private readonly ConcurrentDictionary<string, AppleWebFlow> _web = new();
    private readonly ConcurrentDictionary<string, PendingAppleWebLink> _pending = new();
    public AppleLinkFlow StartLink(string userId, string sessionHash, string stamp)
    {
        Cleanup();
        if (_links.Count >= 1000) throw new VerificationRateLimitException();
        var flow = new AppleLinkFlow(ExternalAccountProofStore.RandomId(), userId, sessionHash, stamp, ExternalAccountProofStore.RandomId(), clock.GetUtcNow().AddMinutes(5));
        _links[flow.Id] = flow; return flow;
    }
    public AppleLinkFlow? TakeLink(string id) => id is { Length: <= 128 } && _links.TryRemove(id, out var flow) && flow.ExpiresAt > clock.GetUtcNow() ? flow : null;
    public AppleLinkFlow? FindLink(string id) => id is { Length: <= 128 } && _links.TryGetValue(id, out var flow) && flow.ExpiresAt > clock.GetUtcNow() ? flow : null;
    public string PendingLink(AppleLinkFlow flow, VerifiedAppleIdentity identity, AppleIdentityTokens tokens, string audience)
    {
        Cleanup(); if (_pending.Count >= 1000) throw new VerificationRateLimitException();
        var ticket = ExternalAccountProofStore.RandomId();
        _pending[ticket] = new(flow, identity, tokens, audience, clock.GetUtcNow().AddMinutes(2)); return ticket;
    }
    public PendingAppleWebLink? TakePending(string ticket) => ticket is { Length: <= 128 } && _pending.TryRemove(ticket, out var pending) && pending.ExpiresAt > clock.GetUtcNow() ? pending : null;
    public AppleWebFlow StartWeb(string correlation, string? returnUrl, string? proofId, AppleLinkFlow? link = null)
    {
        Cleanup(); if (_web.Count >= 1000) throw new VerificationRateLimitException();
        var flow = new AppleWebFlow(ExternalAccountProofStore.RandomId(), ExternalAccountProofStore.Hash(correlation), link?.RawNonce ?? ExternalAccountProofStore.RandomId(), SafeReturnUrl(returnUrl), proofId, link?.Id, clock.GetUtcNow().AddMinutes(5));
        _web[flow.Id] = flow; return flow;
    }
    public AppleWebFlow? TakeWeb(string id, string correlation)
    {
        if (id is not { Length: <= 128 } || !_web.TryGetValue(id, out var flow) || flow.ExpiresAt <= clock.GetUtcNow()
            || flow.CorrelationHash != ExternalAccountProofStore.Hash(correlation)) return null;
        return _web.TryRemove(new(id, flow)) ? flow : null;
    }
    public static string SafeReturnUrl(string? value) => value is { Length: > 0 and <= 1024 } && value[0] == '/'
        && !value.StartsWith("//", StringComparison.Ordinal) && !value.Contains('\\') && !value.Any(char.IsControl) ? value : "/";
    private void Cleanup()
    {
        foreach (var item in _web.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow())) _web.TryRemove(item);
        foreach (var item in _links.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow())) _links.TryRemove(item);
        foreach (var item in _pending.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow())) _pending.TryRemove(item);
    }
}
