using DeyeSolar.Web.Api;
namespace DeyeSolar.Web.Auth;
public interface IAccountSessionStore
{
    Task<MobileSession> CreateAsync(string userId, string userName, string? securityStamp, string? installationId, CancellationToken ct = default);
    Task<MobileSession?> FindAsync(string token, CancellationToken ct = default);
    Task RevokeAsync(string token, CancellationToken ct = default);
    Task RevokeUserAsync(string userId, CancellationToken ct = default);
}
