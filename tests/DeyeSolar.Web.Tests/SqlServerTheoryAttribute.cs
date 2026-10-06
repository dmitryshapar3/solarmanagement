namespace DeyeSolar.Web.Tests;
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOLAR_TEST_SQL_CONNECTION")))
            Skip = "Set SOLAR_TEST_SQL_CONNECTION to run against an isolated SQL Server database.";
    }
}
