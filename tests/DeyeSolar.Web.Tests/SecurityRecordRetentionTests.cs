using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public sealed class SecurityRecordRetentionTests
{
    [SqlServerFact]
    public async Task ExpiredSessionsAreRemovedInBoundedBatchesWithoutRemovingAnActiveSession()
    {
        var database = await SqlServerTestDatabase.CreateAsync("SolarSecurityRetention");
        var options = database.Options;
        await using var db = new DeyeSolarDbContext(options);
        try
        {
            db.Users.Add(new IdentityUser { Id = "retention-owner", UserName = "retention-owner" });
            var now = DateTimeOffset.UtcNow;
            db.AccountSessions.AddRange(Enumerable.Range(0, 501).Select(index => new AccountSessionEntity
            {
                TokenHash = index.ToString("x64"), UserId = "retention-owner", UserName = "retention-owner",
                CreatedAt = now.AddDays(-31).UtcDateTime, ExpiresAt = now.AddSeconds(-1).UtcDateTime
            }));
            db.AccountSessions.Add(new AccountSessionEntity
            {
                TokenHash = "active".PadRight(64, '0'), UserId = "retention-owner", UserName = "retention-owner",
                CreatedAt = now.UtcDateTime, ExpiresAt = now.AddDays(30).UtcDateTime
            });
            await db.SaveChangesAsync();
            var cleaner = new SqlExpiredSessionCleaner(options, new Clock(now));
            Assert.Equal(500, await cleaner.DeleteBatchAsync(CancellationToken.None));
            Assert.Equal(2, await db.AccountSessions.CountAsync());
            Assert.Equal(1, await cleaner.DeleteBatchAsync(CancellationToken.None));
            Assert.Equal(0, await cleaner.DeleteBatchAsync(CancellationToken.None));
            Assert.Equal("active".PadRight(64, '0'), (await db.AccountSessions.AsNoTracking().SingleAsync()).TokenHash);
        }
        finally { await database.DisposeAsync(); }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
