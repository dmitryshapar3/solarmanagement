using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace DeyeSolar.Web.Tests;

public class BrowserBillingDeadlineTests(ITestOutputHelper output)
{
    private const string UserId = "browser-billing-owner";

    [SqlServerFact]
    public async Task OpenCircuitHidesSocketControlsAtDeadlineWhileItsBillingRefreshIsBlockedInSql()
    {
        await using var app = await BrowserBillingTests.ProductionApp.StartAsync(enableApple: true);
        output.WriteLine("Child application assembly: " + app.ApplicationAssemblyIdentity);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { Locale = "en-US" });
        await context.RouteAsync("**/*", route => route.Request.Url.StartsWith(app.Address, StringComparison.Ordinal)
            ? route.ContinueAsync() : route.AbortAsync());
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.GotoAsync(app.Address + "/login");
        await page.Locator("#username").FillAsync("billing-browser@example.test");
        await page.Locator("#password").FillAsync("Browser billing password 42!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign In", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(app.Address + "/");

        // A persisted trust lease can end in twenty seconds on every calendar date,
        // while the Apple period remains far in the future. No receipt is verified or payment made.
        var deadline = await SeedShortTrustedLeaseAsync(app.DatabaseOptions);
        var granted = await context.APIRequest.GetAsync(app.Address + "/api/billing/access");
        Assert.Equal(200, granted.Status);
        using (var access = JsonDocument.Parse(await granted.TextAsync()))
        {
            Assert.True(access.RootElement.GetProperty("hasAccess").GetBoolean());
            Assert.Equal("active", access.RootElement.GetProperty("status").GetString());
            Assert.Equal(deadline, access.RootElement.GetProperty("accessValidUntil").GetDateTimeOffset());
            Assert.True(access.RootElement.GetProperty("subscriptionExpiresAt").GetDateTimeOffset() > deadline.AddDays(20));
        }
        var privateState = await app.ReadPrivateStateAsync();
        var ownBillingState = await ReadOwnBillingStateAsync(app.DatabaseOptions);
        await page.GotoAsync(app.Address + "/devices");
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Refresh devices", Exact = true });
        await Assertions.Expect(refresh).ToBeVisibleAsync();
        // Server-rendered child content can precede the interactive gate's initialization.
        // A reflected layout event proves this circuit has rendered through that gate.
        var drawer = page.Locator(".mud-drawer");
        await Assertions.Expect(drawer).ToHaveClassAsync(new Regex(@"\bmud-drawer--open\b"));
        await AssertLiveDrawerEventAsync(page, drawer);
        output.WriteLine($"Live production circuit before account lock: UTC={DateTimeOffset.UtcNow:O}; AccessValidUntil={deadline:O}");

        await using (var stalled = await BillingAccountLock.AcquireAsync(app.DatabaseOptions))
        {
            await refresh.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Refreshing…", Exact = true }))
                .ToBeVisibleAsync(new() { Timeout = (float)Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds) });
            // Observe the real production SQL reader waiting on our lock before expiry.
            // Holding a lock without this assertion would not prove a stalled refresh.
            var blockedSql = await stalled.WaitForBlockedBillingReadAsync(deadline, app.DiagnosticLogTail);
            output.WriteLine($"Production personal billing read blocked: UTC={DateTimeOffset.UtcNow:O}; AccessValidUntil={deadline:O}; SQL={blockedSql}");
            Assert.True(DateTimeOffset.UtcNow < deadline, "The refresh must already be blocked before access expires.");
            var timeout = (deadline.AddSeconds(2) - DateTimeOffset.UtcNow).TotalMilliseconds;
            Assert.True(timeout > 0);
            await Assertions.Expect(page.GetByText("Your trial has ended. Subscribe to read or control your sockets.",
                new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = (float)timeout });
            await Assertions.Expect(refresh).ToHaveCountAsync(0, new() { Timeout = 500 });
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Refreshing…", Exact = true }))
                .ToHaveCountAsync(0, new() { Timeout = 500 });
            Assert.Equal(app.Address + "/devices", page.Url);
            await Assertions.Expect(page.Locator("a[href='/billing']")).ToBeVisibleAsync(new() { Timeout = 500 });
            output.WriteLine($"Expired access rendered and socket controls hidden: UTC={DateTimeOffset.UtcNow:O}; AccessValidUntil={deadline:O}");
            Assert.True(DateTimeOffset.UtcNow <= deadline.AddSeconds(2), "Socket controls stayed visible beyond the verified lease deadline.");
            await stalled.AssertExclusiveLockHeldAsync();
        }

        var expired = await context.APIRequest.GetAsync(app.Address + "/api/billing/access");
        Assert.Equal(200, expired.Status);
        using var payload = JsonDocument.Parse(await expired.TextAsync());
        Assert.False(payload.RootElement.GetProperty("hasAccess").GetBoolean());
        Assert.Equal("expired", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(ownBillingState, await ReadOwnBillingStateAsync(app.DatabaseOptions));
        Assert.Equal(privateState, await app.ReadPrivateStateAsync());
    }

    private static async Task<DateTimeOffset> SeedShortTrustedLeaseAsync(DbContextOptions<DeyeSolarDbContext> options)
    {
        await using var db = new DeyeSolarDbContext(options);
        var account = await db.BillingAccounts.SingleAsync(a => a.UserId == UserId);
        var now = DateTimeOffset.UtcNow;
        account.TrialStartedAt = now.AddMonths(-2);
        var deadline = now.AddSeconds(20);
        var checkedAt = now.AddHours(-1).AddSeconds(20);
        db.AppleSubscriptions.Add(new AppleSubscription
        {
            OriginalTransactionId = "browser-deadline-original",
            TransactionId = "browser-deadline-transaction",
            UserId = UserId,
            AppAccountToken = account.AppAccountToken,
            Environment = "Sandbox",
            ProductId = "com.dshapar.solar.monthly",
            Status = AppleSubscriptionStatus.Active,
            ExpiresAt = now.AddMonths(1),
            SourceSignedAt = checkedAt,
            CheckedAt = checkedAt,
            ObservationStartedAt = checkedAt
        });
        await db.SaveChangesAsync();
        return deadline;
    }

    private static async Task AssertLiveDrawerEventAsync(IPage page, ILocator drawer)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < timeout)
        {
            if (Regex.IsMatch(await drawer.GetAttributeAsync("class") ?? string.Empty, @"\bmud-drawer--closed\b")) return;
            await page.Locator(".mud-appbar button").First.ClickAsync();
            try
            {
                await Assertions.Expect(drawer).ToHaveClassAsync(new Regex(@"\bmud-drawer--closed\b"), new() { Timeout = 500 });
                return;
            }
            catch (PlaywrightException) when (DateTimeOffset.UtcNow < timeout) { }
        }
        Assert.Fail("The production Blazor circuit did not reflect the drawer event before the billing lock.");
    }

    private static async Task<string> ReadOwnBillingStateAsync(DbContextOptions<DeyeSolarDbContext> options)
    {
        await using var db = new DeyeSolarDbContext(options);
        return JsonSerializer.Serialize(new
        {
            Account = await db.BillingAccounts.AsNoTracking().SingleAsync(a => a.UserId == UserId),
            Subscriptions = await db.AppleSubscriptions.AsNoTracking().Where(s => s.UserId == UserId)
                .OrderBy(s => s.OriginalTransactionId).ToArrayAsync()
        });
    }

    private sealed class BillingAccountLock(SqlConnection connection, SqlTransaction transaction, int sessionId,
        string connectionString) : IAsyncDisposable
    {
        public static async Task<BillingAccountLock> AcquireAsync(DbContextOptions<DeyeSolarDbContext> options)
        {
            await using var db = new DeyeSolarDbContext(options);
            var connectionString = db.Database.GetConnectionString()!;
            var connection = new SqlConnection(connectionString);
            SqlTransaction? transaction = null;
            try
            {
                await connection.OpenAsync();
                await using (var isolation = connection.CreateCommand())
                {
                    isolation.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";
                    Assert.False(Convert.ToBoolean(await isolation.ExecuteScalarAsync()),
                        "The isolated deadline fixture requires READ_COMMITTED_SNAPSHOT OFF after production startup.");
                }
                transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                // An exclusive table lock stalls every account read regardless of the optimizer's index choice.
                // The database belongs only to this fixture; independent browser fixtures remain unaffected.
                command.CommandText = """
                    SELECT [UserId], [AppAccountToken], [TrialStartedAt]
                    FROM [BillingAccounts] WITH (TABLOCKX, HOLDLOCK)
                    """;
                var ownerFound = false;
                await using (var reader = await command.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) ownerFound |= reader.GetString(0) == UserId;
                Assert.True(ownerFound, "The locked billing table must contain the authenticated account.");
                command.CommandText = "SELECT @@SPID";
                var sessionId = Convert.ToInt32(await command.ExecuteScalarAsync());
                return new(connection, transaction, sessionId, connectionString);
            }
            catch
            {
                if (transaction is not null) await transaction.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task<string> WaitForBlockedBillingReadAsync(DateTimeOffset deadline, Func<string> productionLogs)
        {
            await using var observer = new SqlConnection(connectionString);
            await observer.OpenAsync();
            await using var command = observer.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT TOP (1) statement.text FROM sys.dm_exec_requests AS request
                CROSS APPLY sys.dm_exec_sql_text(request.sql_handle) AS statement
                WHERE request.blocking_session_id = @sessionId
                    AND request.wait_type LIKE N'LCK_M_%'
                    AND CHARINDEX(N'FROM [BillingAccounts] AS [b]', statement.text) > 0
                    AND CHARINDEX(N'WHERE [b].[UserId] = @', statement.text) > 0
                """;
            command.Parameters.Add("@sessionId", SqlDbType.Int).Value = sessionId;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await command.ExecuteScalarAsync() is string blockedSql) return blockedSql;
                await Task.Delay(50);
            }
            throw new Xunit.Sdk.XunitException("No production personal BillingAccounts refresh was observed blocked before the lease expired. "
                + await ReadDiagnosticsAsync(observer) + " Production SQL log: " + productionLogs());
        }

        private async Task<string> ReadDiagnosticsAsync(SqlConnection observer)
        {
            await using var command = observer.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();
                SELECT request.session_id, request.blocking_session_id, request.wait_type, request.status,
                    session.transaction_isolation_level, statement.text, request.database_id, statement.dbid
                FROM sys.dm_exec_requests AS request
                JOIN sys.dm_exec_sessions AS session ON session.session_id = request.session_id
                CROSS APPLY sys.dm_exec_sql_text(request.sql_handle) AS statement
                WHERE request.session_id <> @@SPID AND (request.blocking_session_id = @sessionId
                    OR (request.database_id = DB_ID() OR statement.dbid = DB_ID())
                        AND CHARINDEX(N'BillingAccounts', statement.text) > 0);
                SELECT held.request_session_id, held.resource_type, held.request_mode, held.request_status,
                    held.resource_associated_entity_id, object.name, indexDefinition.name
                FROM sys.dm_tran_locks AS held
                LEFT JOIN sys.partitions AS partition ON partition.hobt_id = held.resource_associated_entity_id
                LEFT JOIN sys.objects AS object ON object.object_id = CASE WHEN held.resource_type = N'OBJECT'
                    THEN held.resource_associated_entity_id ELSE partition.object_id END
                LEFT JOIN sys.indexes AS indexDefinition ON indexDefinition.object_id = partition.object_id
                    AND indexDefinition.index_id = partition.index_id
                WHERE held.resource_database_id = DB_ID()
                    AND (held.request_session_id = @sessionId OR held.request_status = N'WAIT');
                """;
            command.Parameters.Add("@sessionId", SqlDbType.Int).Value = sessionId;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var snapshotReads = reader.GetBoolean(0);
            await reader.NextResultAsync();
            var requests = new List<object>();
            while (await reader.ReadAsync())
                requests.Add(new
                {
                    Session = reader.GetInt16(0),
                    BlockedBy = reader.GetInt16(1),
                    Wait = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Status = reader.GetString(3),
                    Isolation = reader.GetInt16(4),
                    Sql = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RequestDatabase = Convert.ToInt32(reader.GetValue(6)),
                    SqlDatabase = reader.IsDBNull(7) ? (int?)null : Convert.ToInt32(reader.GetValue(7))
                });
            await reader.NextResultAsync();
            var locks = new List<object>();
            while (await reader.ReadAsync())
                locks.Add(new
                {
                    Session = reader.GetInt32(0),
                    Resource = reader.GetString(1),
                    Mode = reader.GetString(2),
                    Status = reader.GetString(3),
                    Entity = reader.GetInt64(4),
                    Table = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Index = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            return JsonSerializer.Serialize(new { LockSession = sessionId, ReadCommittedSnapshot = snapshotReads, Requests = requests, Locks = locks });
        }

        public async Task AssertExclusiveLockHeldAsync()
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE request_session_id = @@SPID AND resource_database_id = DB_ID()
                    AND resource_type = N'OBJECT' AND resource_associated_entity_id = OBJECT_ID(N'BillingAccounts')
                    AND request_mode = N'X' AND request_status = N'GRANT'
                """;
            Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) > 0,
                "The billing table lock must remain held while the browser hides cached socket controls.");
        }

        public async ValueTask DisposeAsync()
        {
            try { await transaction.RollbackAsync(); }
            finally
            {
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }
}
