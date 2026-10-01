using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Tenancy;

/// <summary>A runtime's installation is immutable for its entire lifetime.</summary>
public sealed class TenantDbContextFactory(DbContextOptions<DeyeSolarDbContext> options, string installationId)
    : IDbContextFactory<DeyeSolarDbContext>
{
    public string InstallationId { get; } = ValidateId(installationId);
    public DeyeSolarDbContext CreateDbContext() => new(options, InstallationId);
    public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
    internal static string ValidateId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && !value.Any(char.IsControl)
        ? value : throw new ArgumentException("A valid installation is required.", nameof(value));
}
