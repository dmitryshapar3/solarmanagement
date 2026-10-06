using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
namespace DeyeSolar.Web.Api;
public sealed record MobileSession(string Token, string UserId, string UserName, DateTimeOffset ExpiresAt,
    string? SecurityStamp = null, string? InstallationId = null)
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; }
}
// The parameterless store is an explicit test adapter. Production registration uses Persistent().
public class MobileSessionStore : IAccountSessionStore
{
    public const int MaximumSessionsPerUser = 10;
    private readonly ConcurrentDictionary<string, MobileSession> _sessions = new();
    private readonly DbContextOptions<DeyeSolarDbContext>? _database;
    private readonly TimeProvider _clock;
    public bool IsPersistent => _database is not null;
    public MobileSessionStore() => _clock = TimeProvider.System;
    private MobileSessionStore(DbContextOptions<DeyeSolarDbContext> database, TimeProvider clock)
        => (_database, _clock) = (database, clock);
    public static MobileSessionStore Persistent(DbContextOptions<DeyeSolarDbContext> database, TimeProvider clock)
        => new(database, clock);
    public async Task<MobileSession> CreateAsync(string userId, string userName, string? securityStamp = null,
        string? installationId = null, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var session = new MobileSession(token, userId, userName, now.AddDays(30), securityStamp, installationId) { CreatedAt = now };
        if (_database is null)
        {
            lock (_sessions)
            {
                foreach (var old in _sessions.Where(s => s.Value.ExpiresAt <= now).Select(s => s.Key).ToArray()) _sessions.TryRemove(old, out _);
                var oldSessions = _sessions.Where(s => s.Value.UserId == userId).OrderBy(s => s.Value.ExpiresAt).ToArray();
                foreach (var old in oldSessions.Take(Math.Max(0, oldSessions.Length - MaximumSessionsPerUser + 1))) _sessions.TryRemove(old.Key, out _);
                _sessions[token] = session;
            }
            return session;
        }
        await using var db = new DeyeSolarDbContext(_database);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = db.Database.IsSqlServer()
            ? await db.Users.FromSqlInterpolated($"SELECT * FROM [AspNetUsers] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {userId}").SingleOrDefaultAsync(ct)
            : await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || securityStamp != user.SecurityStamp) throw new UnauthorizedAccessException("The account session is no longer valid.");
        var existing = await db.AccountSessions.Where(s => s.UserId == userId).OrderBy(s => s.CreatedAt).ThenBy(s => s.TokenHash).ToListAsync(ct);
        var expired = existing.Where(s => s.ExpiresAt <= now.UtcDateTime).ToArray();
        db.AccountSessions.RemoveRange(expired);
        var active = existing.Except(expired).ToArray();
        db.AccountSessions.RemoveRange(active.Take(Math.Max(0, active.Length - MaximumSessionsPerUser + 1)));
        db.AccountSessions.Add(new() { SessionId = session.SessionId, TokenHash = Hash(token), UserId = userId, UserName = userName,
            SecurityStamp = securityStamp, InstallationId = installationId, CreatedAt = now.UtcDateTime, ExpiresAt = session.ExpiresAt.UtcDateTime });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return session;
    }
    public async Task<MobileSession?> FindAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64 || !token.All(char.IsAsciiHexDigit)) return null;
        if (_database is null)
        {
            if (!_sessions.TryGetValue(token, out var session)) return null;
            if (session.ExpiresAt > _clock.GetUtcNow()) return session;
            _sessions.TryRemove(token, out _);
            return null;
        }
        await using var db = new DeyeSolarDbContext(_database);
        var hash = Hash(token);
        var saved = await db.AccountSessions.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (saved is null || saved.ExpiresAt <= _clock.GetUtcNow().UtcDateTime) return null;
        var now = _clock.GetUtcNow().UtcDateTime;
        if (saved.LastSeenAt is null || saved.LastSeenAt < now.AddMinutes(-5))
            await db.AccountSessions.Where(s => s.TokenHash == hash && (s.LastSeenAt == null || s.LastSeenAt < now.AddMinutes(-5)))
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.LastSeenAt, now), ct);
        return new(token, saved.UserId, saved.UserName, new(DateTime.SpecifyKind(saved.ExpiresAt, DateTimeKind.Utc)), saved.SecurityStamp, saved.InstallationId)
            { SessionId = saved.SessionId, CreatedAt = Utc(saved.CreatedAt) };
    }
    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        if (_database is null) { _sessions.TryRemove(token, out _); return; }
        if (string.IsNullOrEmpty(token) || token.Length > 128) return;
        await using var db = new DeyeSolarDbContext(_database);
        var hash = Hash(token);
        await db.AccountSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(ct);
    }
    public async Task RevokeUserAsync(string userId, CancellationToken ct = default)
    {
        if (_database is null)
        {
            lock (_sessions)
                foreach (var token in _sessions.Where(s => s.Value.UserId == userId).Select(s => s.Key).ToArray()) _sessions.TryRemove(token, out _);
            return;
        }
        await using var db = new DeyeSolarDbContext(_database);
        await db.AccountSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    public async Task<IReadOnlyList<AccountSessionInfo>> ListAsync(string userId, string currentToken, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        if (_database is null) return _sessions.Values.Where(s => s.UserId == userId && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt).Select(s => new AccountSessionInfo(s.SessionId, null, null, null, s.CreatedAt, s.Token == currentToken)).ToArray();
        await using var db = new DeyeSolarDbContext(_database);
        var currentHash = Hash(currentToken);
        var rows = await db.AccountSessions.AsNoTracking().Where(s => s.UserId == userId && s.ExpiresAt > now.UtcDateTime)
            .OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        return rows.Select(s => new AccountSessionInfo(s.SessionId, s.Platform, s.Client,
            s.LastSeenAt is { } seen ? Utc(seen) : null, Utc(s.CreatedAt), s.TokenHash == currentHash)).ToArray();
    }
    public async Task<bool> RevokeSessionAsync(string userId, Guid sessionId, CancellationToken ct = default)
    {
        if (_database is null)
        {
            var token = _sessions.FirstOrDefault(s => s.Value.UserId == userId && s.Value.SessionId == sessionId).Key;
            return token is not null && _sessions.TryRemove(token, out _);
        }
        await using var db = new DeyeSolarDbContext(_database);
        return await db.AccountSessions.Where(s => s.UserId == userId && s.SessionId == sessionId).ExecuteDeleteAsync(ct) > 0;
    }
    public async Task RevokeOthersAsync(string userId, string currentToken, CancellationToken ct = default)
    {
        if (_database is null)
        {
            lock (_sessions) foreach (var token in _sessions.Where(s => s.Value.UserId == userId && s.Key != currentToken).Select(s => s.Key).ToArray()) _sessions.TryRemove(token, out _);
            return;
        }
        await using var db = new DeyeSolarDbContext(_database);
        var currentHash = Hash(currentToken);
        await db.AccountSessions.Where(s => s.UserId == userId && s.TokenHash != currentHash).ExecuteDeleteAsync(ct);
    }
    public MobileSession Create(string userId, string userName, string? securityStamp = null, string? installationId = null)
        => CreateAsync(userId, userName, securityStamp, installationId).GetAwaiter().GetResult();
    public MobileSession? Find(string token) => FindAsync(token).GetAwaiter().GetResult();
    public void Revoke(string token) => RevokeAsync(token).GetAwaiter().GetResult();
}
