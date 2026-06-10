using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DeyeSolar.Web.Api;

public sealed record MobileSession(
    string Token,
    string UserId,
    string UserName,
    DateTimeOffset ExpiresAt);

public class MobileSessionStore
{
    private readonly ConcurrentDictionary<string, MobileSession> _sessions = new();

    public MobileSession Create(string userId, string userName)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var session = new MobileSession(
            token,
            userId,
            userName,
            DateTimeOffset.UtcNow.AddDays(30));

        _sessions[token] = session;
        return session;
    }

    public MobileSession? Find(string token)
    {
        if (!_sessions.TryGetValue(token, out var session))
            return null;

        if (session.ExpiresAt > DateTimeOffset.UtcNow)
            return session;

        _sessions.TryRemove(token, out _);
        return null;
    }

    public void Revoke(string token)
        => _sessions.TryRemove(token, out _);
}
