using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tests;

internal sealed class TestDbFactory : IDbContextFactory<DeyeSolarDbContext>
{
    private readonly DbContextOptions<DeyeSolarDbContext> _options;

    private TestDbFactory(DbContextOptions<DeyeSolarDbContext> options)
    {
        _options = options;
    }

    public static IDbContextFactory<DeyeSolarDbContext> Create(string databaseName)
    {
        var options = new DbContextOptionsBuilder<DeyeSolarDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new TestDbFactory(options);
    }

    public DeyeSolarDbContext CreateDbContext()
        => new(_options);

    public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}
