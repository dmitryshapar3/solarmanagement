using DeyeSolar.Web.Data;
using DeyeSolar.Web.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

internal enum SqlTestSchema { Migrations, Model, None }

/// <summary>Owns an isolated SQL database, including cleanup when initialization does not return a fixture.</summary>
internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private SqlServerTestDatabase(DbContextOptions<DeyeSolarDbContext> options)
    {
        Options = options;
        Factory = new(options, TestInstallation.Id);
    }
    public DbContextOptions<DeyeSolarDbContext> Options { get; }
    public TenantDbContextFactory Factory { get; }

    public static async Task<SqlServerTestDatabase> CreateAsync(string prefix, SqlTestSchema schema = SqlTestSchema.Migrations,
        Action<DbContextOptionsBuilder<DeyeSolarDbContext>>? configureOptions = null, Func<DeyeSolarDbContext, Task>? seed = null)
    {
        var server = Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(server)) throw new InvalidOperationException("SQL test configuration is required.");
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 90 || prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("Use a short SQL fixture name with letters, digits or underscores.", nameof(prefix));
        var connection = new SqlConnectionStringBuilder(server) { InitialCatalog = prefix + "_" + Guid.NewGuid().ToString("N") };
        var builder = new DbContextOptionsBuilder<DeyeSolarDbContext>().UseSqlServer(connection.ConnectionString);
        configureOptions?.Invoke(builder);
        var database = new SqlServerTestDatabase(builder.Options);
        try
        {
            await using var db = database.Factory.CreateDbContext();
            switch (schema)
            {
                case SqlTestSchema.Migrations: await db.Database.MigrateAsync(); break;
                case SqlTestSchema.Model: await db.Database.EnsureCreatedAsync(); break;
                case SqlTestSchema.None: break;
                default: throw new ArgumentOutOfRangeException(nameof(schema));
            }
            if (seed is not null) await seed(db);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = Factory.CreateDbContext();
        await db.Database.EnsureDeletedAsync();
    }
}
