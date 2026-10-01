using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tenancy;

// Identity and membership queries may run before binding; private queries then fail closed.
public sealed class RequestDbContextFactory(DbContextOptions<DeyeSolarDbContext> options,
    CurrentInstallation current) : IDbContextFactory<DeyeSolarDbContext>
{
    public DeyeSolarDbContext CreateDbContext() => current.Id is { } id
        ? new DeyeSolarDbContext(options, id) : new DeyeSolarDbContext(options);

    public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}
