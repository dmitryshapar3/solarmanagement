using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

public sealed class SqlServerTestDatabaseTests
{
    [SqlServerFact]
    public async Task FailedFixtureSeedDoesNotLeaveItsCreatedDatabaseBehind()
    {
        string? createdName = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqlServerTestDatabase.CreateAsync("SolarFailedFixture",
            seed: db =>
            {
                createdName = db.Database.GetDbConnection().Database;
                throw new InvalidOperationException("Injected fixture initialization failure.");
            }));
        Assert.NotNull(createdName);
        var server = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION")) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(server.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
        command.Parameters.AddWithValue("name", createdName);
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
    }
}
