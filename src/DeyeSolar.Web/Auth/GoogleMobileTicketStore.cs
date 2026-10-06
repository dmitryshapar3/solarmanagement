using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace DeyeSolar.Web.Auth;

public sealed record GoogleMobileFlow(string CodeChallenge, string State, string? LinkingUserId);
public sealed record GoogleMobileExchangeRequest(string Code, string CodeVerifier);
public sealed record GoogleMobileLinkStartRequest(string CodeChallenge, string State, AccountSecurityProof? Proof = null);
public sealed record PendingGoogleLink(string UserId, string Subject, string Email);
public sealed record GoogleMobileProof(string? UserId, PendingGoogleLink? Link);

public sealed class GoogleMobileTicketStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new();
    private readonly ConcurrentDictionary<string, (GoogleMobileFlow Flow, DateTimeOffset ExpiresAt)> _links = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _callbacks = new();
    public static bool ValidFlow(string challenge, string state) => ValidToken(challenge, 43, 43) && ValidToken(state, 16, 128);
    private static bool ValidToken(string? value, int min, int max) => value is not null && value.Length >= min && value.Length <= max
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public string StartLink(GoogleMobileFlow flow)
    {
        Cleanup();
        if (_links.Count >= 10000) throw new VerificationRateLimitException();
        var token = NewToken();
        _links[Hash(token)] = (flow, clock.GetUtcNow().AddMinutes(2));
        return token;
    }

    public string StartCallback()
    {
        Cleanup();
        if (_callbacks.Count >= 10000) throw new VerificationRateLimitException();
        var key = NewToken();
        _callbacks[Hash(key)] = clock.GetUtcNow().AddMinutes(10);
        return key;
    }

    public bool TakeCallback(string key) => ValidToken(key, 43, 43)
        && _callbacks.TryRemove(Hash(key), out var expiresAt) && expiresAt > clock.GetUtcNow();

    public GoogleMobileFlow? TakeLink(string token)
        => ValidToken(token, 43, 43) && _links.TryRemove(Hash(token), out var link) && link.ExpiresAt > clock.GetUtcNow() ? link.Flow : null;

    public string Create(string userId, GoogleMobileFlow flow)
    {
        Cleanup();
        if (_tickets.Count >= 10000) throw new VerificationRateLimitException();
        var code = NewToken();
        _tickets[Hash(code)] = new(new(userId, null), flow.CodeChallenge, clock.GetUtcNow().AddMinutes(2));
        return code;
    }

    public string CreatePendingLink(string subject, string email, GoogleMobileFlow flow)
    {
        if (flow.LinkingUserId is null) throw new ArgumentException("A linking account is required.");
        Cleanup();
        if (_tickets.Count >= 10000) throw new VerificationRateLimitException();
        var code = NewToken();
        _tickets[Hash(code)] = new(new(null, new(flow.LinkingUserId, subject, email)), flow.CodeChallenge, clock.GetUtcNow().AddMinutes(2));
        return code;
    }

    public GoogleMobileProof? Exchange(string code, string verifier, string? authenticatedUserId = null)
    {
        if (!ValidToken(code, 43, 43) || verifier is null || verifier.Length is < 43 or > 128
            || !verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')) return null;
        var key = Hash(code);
        if (!_tickets.TryGetValue(key, out var ticket) || ticket.ExpiresAt <= clock.GetUtcNow()) return null;
        if (ticket.Proof.Link is not null && ticket.Proof.Link.UserId != authenticatedUserId) return null;
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ticket.Challenge), Encoding.ASCII.GetBytes(challenge))) return null;
        return _tickets.TryRemove(key, out var consumed) && consumed == ticket ? consumed.Proof : null;
    }

    private void Cleanup()
    {
        var now = clock.GetUtcNow();
        foreach (var key in _tickets.Where(t => t.Value.ExpiresAt <= now).Select(t => t.Key)) _tickets.TryRemove(key, out _);
        foreach (var key in _links.Where(t => t.Value.ExpiresAt <= now).Select(t => t.Key)) _links.TryRemove(key, out _);
        foreach (var key in _callbacks.Where(t => t.Value <= now).Select(t => t.Key)) _callbacks.TryRemove(key, out _);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    private static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private sealed record Ticket(GoogleMobileProof Proof, string Challenge, DateTimeOffset ExpiresAt);
}
