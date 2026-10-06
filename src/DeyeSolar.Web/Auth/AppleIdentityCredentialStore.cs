using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Auth;

public sealed class AppleIdentityCredentialStore(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Solar.Auth.Apple.RefreshToken.v1");
    public async Task SaveAsync(DeyeSolarDbContext db, string userId, string subject, string audience, string refreshToken, CancellationToken ct)
    {
        var saved = await db.AppleIdentityCredentials.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (saved is null) { saved = new() { UserId = userId }; db.AppleIdentityCredentials.Add(saved); }
        else if (saved.Subject != subject) throw new AccountIdentityException("link_conflict", "This account already has another Apple identity.");
        saved.Subject = subject; saved.Audience = audience; saved.ProtectedRefreshToken = _protector.Protect(refreshToken); saved.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }
    public async Task QueueRevokeAsync(DeyeSolarDbContext db, string userId, CancellationToken ct)
    {
        var saved = await db.AppleIdentityCredentials.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (saved is null) return;
        db.AppleIdentityRevocations.Add(new() { Id = Guid.NewGuid(), Audience = saved.Audience, ProtectedRefreshToken = saved.ProtectedRefreshToken, NextAttemptAt = clock.GetUtcNow().UtcDateTime });
        db.AppleIdentityCredentials.Remove(saved);
        await db.SaveChangesAsync(ct);
    }
    public string Unprotect(string token) => _protector.Unprotect(token);
}
public sealed class AppleIdentityRevocationWorker(DbContextOptions<DeyeSolarDbContext> database, AppleIdentityCredentialStore credentials,
    IAppleIdentityTokenClient tokens, AuthProviderOptions providers, TimeProvider clock, ILogger<AppleIdentityRevocationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (providers.Apple.NativeAvailable) await ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Apple identity revocation retry is pending."); }
            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
        }
    }
    public async Task ProcessAsync(CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(database);
        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await db.AppleIdentityRevocations.Where(x => x.NextAttemptAt <= now).OrderBy(x => x.NextAttemptAt).Take(20).ToListAsync(ct);
        foreach (var work in pending)
        {
            try { await tokens.RevokeAsync(credentials.Unprotect(work.ProtectedRefreshToken), work.Audience, ct); db.AppleIdentityRevocations.Remove(work); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { work.Attempts = Math.Min(work.Attempts + 1, 1000); work.NextAttemptAt = now.AddMinutes(Math.Min(1440, Math.Pow(2, Math.Min(work.Attempts, 11)))); }
            await db.SaveChangesAsync(ct);
        }
    }
}
