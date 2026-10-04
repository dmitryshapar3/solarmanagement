using System.Data;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
namespace DeyeSolar.Web.Auth;
/// <summary>Run once after migrations and before runtimes/HTTP start. Interrupted deletion
/// restores its recorded admission state; account data is never deleted by recovery.</summary>
public sealed class AccountOffboardingRecovery(DbContextOptions<DeyeSolarDbContext> database)
{
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        await using var db = new DeyeSolarDbContext(database);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var recovered = await db.Installations.Where(i => i.OffboardingUserId != null).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.IsEnabled, i => i.OffboardingWasEnabled ?? false)
            .SetProperty(i => i.OffboardingUserId, (string?)null)
            .SetProperty(i => i.OffboardingWasEnabled, (bool?)null), ct);
        await transaction.CommitAsync(ct);
        return recovered;
    }
}
