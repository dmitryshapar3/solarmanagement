using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Data;

public sealed class InstallationMembershipService(IDbContextFactory<DeyeSolarDbContext> factory)
{
    public async Task<InstallationMembership?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId)) return null;
        var selected = principal.FindFirstValue(InstallationIds.ClaimType);
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.InstallationMemberships.AsNoTracking().Include(m => m.Installation)
            .Where(m => m.UserId == userId && m.Installation.IsEnabled);
        if (selected is not null) query = query.Where(m => m.InstallationId == selected);
        return await query.OrderBy(m => m.Installation.CreatedAt).ThenBy(m => m.InstallationId).FirstOrDefaultAsync(ct);
    }

    public async Task<InstallationMembership?> GetForUserAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.InstallationMemberships.AsNoTracking().Include(m => m.Installation)
            .Where(m => m.UserId == userId && m.Installation.IsEnabled)
            .OrderBy(m => m.Installation.CreatedAt).ThenBy(m => m.InstallationId).FirstOrDefaultAsync(ct);
    }
}
