using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DeyeSolar.Web.Tests;

public sealed class RedesignMigrationTests
{
    [SqlServerFact]
    public async Task UpgradePreservesDurableSessionTokensAndAssignsUniqueOpaqueIdsOnce()
    {
        await using var database=await SqlServerTestDatabase.CreateAsync("RedesignUpgrade",SqlTestSchema.None);
        await using var db=database.Factory.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261005120000_RestoreProvenSolarHistoryQuality");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO AspNetUsers(Id,UserName,NormalizedUserName,SecurityStamp,ConcurrencyStamp,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount)
            VALUES('synthetic-owner','owner','OWNER','unchanged-stamp','concurrency',0,0,0,0,0);
            INSERT INTO AccountSessions(TokenHash,UserId,UserName,SecurityStamp,CreatedAt,ExpiresAt)
            VALUES(REPLICATE('A',64),'synthetic-owner','owner','unchanged-stamp','2026-10-01','2026-11-01'),
                  (REPLICATE('B',64),'synthetic-owner','owner','unchanged-stamp','2026-10-02','2026-11-02');
            """);
        await db.Database.MigrateAsync();var rows=await db.AccountSessions.AsNoTracking().OrderBy(s=>s.TokenHash).ToListAsync();
        Assert.Equal(2,rows.Count);Assert.Equal(2,rows.Select(s=>s.SessionId).Distinct().Count());Assert.DoesNotContain(rows,r=>r.SessionId==Guid.Empty);
        Assert.Equal(new string('A',64),rows[0].TokenHash);Assert.Equal(new string('B',64),rows[1].TokenHash);Assert.All(rows,r=>Assert.Equal("unchanged-stamp",r.SecurityStamp));
        var ids=rows.Select(s=>s.SessionId).ToArray();await db.Database.MigrateAsync();
        Assert.Equal(ids,(await db.AccountSessions.AsNoTracking().OrderBy(s=>s.TokenHash).ToListAsync()).Select(s=>s.SessionId));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
