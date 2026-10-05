using System.Text.RegularExpressions;
using DeyeSolar.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Operations;

/// <summary>Used only by the privileged migration job; runtime receives a separate DML-only login.</summary>
internal static class RuntimeDatabaseProvisioner
{
    public static async Task ProvisionAsync(DeyeSolarDbContext db, IConfiguration configuration, CancellationToken ct)
    {
        var user = configuration["Operations:RuntimeDatabaseUser"];
        if (string.IsNullOrWhiteSpace(user)) return;
        if (!Regex.IsMatch(user, "\\A[a-zA-Z][a-zA-Z0-9_]{0,63}\\z")) throw new InvalidOperationException("Runtime database user must be a simple SQL identifier.");
        var path = configuration["Operations:RuntimeDatabasePasswordFile"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Runtime database password file must be absolute.");
        var password = (await File.ReadAllTextAsync(path, ct)).TrimEnd('\r', '\n');
        if (password.Length < 20) throw new InvalidOperationException("Runtime database password must contain at least 20 characters.");
        // Values remain parameters. Dynamic SQL escapes identifiers and password literals inside SQL Server.
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @quotedName nvarchar(130) = QUOTENAME(@runtimeName);
            DECLARE @sql nvarchar(max);
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @runtimeName)
            BEGIN
                SET @sql = N'CREATE LOGIN ' + @quotedName + N' WITH PASSWORD = N''' + REPLACE(@runtimePassword, N'''', N'''''') + N''', CHECK_POLICY = ON';
                EXEC (@sql);
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @runtimeName)
            BEGIN
                SET @sql = N'CREATE USER ' + @quotedName + N' FOR LOGIN ' + @quotedName;
                EXEC (@sql);
            END;
            SET @sql = N'ALTER USER ' + @quotedName + N' WITH LOGIN = ' + @quotedName;
            EXEC (@sql);
            SET @sql = N'GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO ' + @quotedName;
            EXEC (@sql);
            SET @sql = N'GRANT VIEW DEFINITION ON OBJECT::dbo.TriggerRules TO ' + @quotedName;
            EXEC (@sql);
            """, [new SqlParameter("runtimeName", user), new SqlParameter("runtimePassword", password)], ct);
    }
}
