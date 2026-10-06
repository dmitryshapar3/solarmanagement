using DeyeSolar.Web.Api;
namespace DeyeSolar.Web.Auth;
public interface IAccountSessionStore
{
    Task<MobileSession> CreateAsync(string userId, string userName, string? securityStamp, string? installationId, CancellationToken ct = default);
    Task<MobileSession?> FindAsync(string token, CancellationToken ct = default);
    Task RevokeAsync(string token, CancellationToken ct = default);
    Task RevokeUserAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<AccountSessionInfo>> ListAsync(string userId, string currentToken, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccountSessionInfo>>(Array.Empty<AccountSessionInfo>());
    Task<bool> RevokeSessionAsync(string userId, Guid sessionId, CancellationToken ct = default)
        => Task.FromResult(false);
    Task RevokeOthersAsync(string userId, string currentToken, CancellationToken ct = default)
        => throw new NotSupportedException();
}
public sealed record AccountSessionInfo(Guid Id, string? Platform, string? Client, DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt, bool IsCurrent);
