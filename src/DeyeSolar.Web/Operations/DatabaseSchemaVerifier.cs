using DeyeSolar.Web.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Operations;

public static class DatabaseSchemaVerifier
{
    public static async Task EnsureCompatibleMigrationHistoryAsync(DeyeSolarDbContext db, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        if (applied.Except(known).Any())
            throw new InvalidOperationException("Database schema belongs to a newer release. Use a compatible application image.");
    }

    public static async Task VerifyAsync(DeyeSolarDbContext db, bool requireLeastPrivilege, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (known.Except(applied).Any())
            throw new InvalidOperationException("Database schema is behind this release. Run the privileged migration job first.");
        if (applied.Except(known).Any())
            throw new InvalidOperationException("Database schema belongs to a newer release. Use a compatible application image.");
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
}
