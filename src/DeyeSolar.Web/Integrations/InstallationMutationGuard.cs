using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

/// <summary>Locks installation admission before instance, quota or rule locks; offboarding updates this same row.</summary>
internal static class InstallationMutationGuard
{
    public static async Task EnsureEnabledAsync(DeyeSolarDbContext db, CancellationToken ct)
    {
        var id = db.InstallationId ?? throw new InvalidOperationException("An installation is required.");
        var query = db.Installations.AsNoTracking();
        if (db.Database.IsSqlServer())
            query = db.Installations.FromSqlInterpolated($"SELECT * FROM [Installations] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").AsNoTracking();
        if (!await query.AnyAsync(installation => installation.Id == id && installation.IsEnabled, ct))
            throw new DeyeSolar.Web.Auth.InstallationAccessException("The installation is unavailable.");
    }
}
