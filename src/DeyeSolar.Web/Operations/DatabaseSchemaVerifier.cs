using DeyeSolar.Web.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Operations;

public static class DatabaseSchemaVerifier
{
    private const string HistoricalFailureCounterMigration = "20260418223955_AddRuleConsecutiveFailures";

    public static async Task EnsureCompatibleMigrationHistoryAsync(DeyeSolarDbContext db, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        await VerifyRecordedMigrationHistoryAsync(db, known, applied, ct);
    }

    public static async Task VerifyAsync(DeyeSolarDbContext db, bool requireLeastPrivilege, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (known.Except(applied).Any())
            throw new InvalidOperationException("Database schema is behind this release. Run the privileged migration job first.");
        await VerifyRecordedMigrationHistoryAsync(db, known, applied, ct);
        // Migration history alone cannot detect a missing table/column after manual database changes.
        var expected = JsonSerializer.Serialize(db.Model.GetRelationalModel().Tables.SelectMany(table => table.Columns.Select(column =>
            new { schema = table.Schema ?? "dbo", table = table.Name, column = column.Name })));
        var missing = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value]
            FROM OPENJSON(@expectedSchema) WITH ([schema] nvarchar(128), [table] nvarchar(128), [column] nvarchar(128)) expected
            LEFT JOIN sys.schemas s ON s.name = expected.[schema]
            LEFT JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = expected.[table]
            LEFT JOIN sys.columns c ON c.object_id = t.object_id AND c.name = expected.[column]
            WHERE c.column_id IS NULL
            """, new SqlParameter("expectedSchema", expected)).SingleAsync(ct);
        if (missing != 0) throw new InvalidOperationException("Database schema is missing application tables or columns.");
        if (requireLeastPrivilege)
        {
            var elevated = await db.Database.SqlQueryRaw<int>("""
                SELECT CONVERT(int, CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1 THEN 1 ELSE 0 END) AS [Value]
                """).SingleAsync(ct);
            if (elevated != 0) throw new InvalidOperationException("Runtime database login must not have administrative or schema modification permissions.");
        }
    }

    private static async Task VerifyRecordedMigrationHistoryAsync(DeyeSolarDbContext db, HashSet<string> known,
        IEnumerable<string> applied, CancellationToken ct)
    {
        var unknown = applied.Except(known, StringComparer.Ordinal).ToArray();
        if (unknown.Length == 0) return;
        if (unknown.Length != 1 || unknown[0] != HistoricalFailureCounterMigration)
            throw new InvalidOperationException("Database schema belongs to a newer release. Use a compatible application image.");
        // Earlier deployments retain this migration and its unmapped counter. Recognize
        // only that recorded schema; validation must never rewrite its history or values.
        var supported = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value] FROM sys.columns c
            JOIN sys.default_constraints d ON d.object_id=c.default_object_id
            WHERE c.object_id=OBJECT_ID(N'dbo.TriggerRules') AND c.name=N'ConsecutiveFailures'
                AND c.system_type_id=56 AND c.user_type_id=TYPE_ID(N'int') AND c.is_nullable=0
                AND c.max_length=4 AND c.precision=10 AND c.scale=0
                AND REPLACE(REPLACE(d.definition,N'(',N''),N')',N'')=N'0'
            """).SingleAsync(ct);
        if (supported != 1)
            throw new InvalidOperationException("The historical failure-counter migration does not match its recorded schema.");
    }
}
