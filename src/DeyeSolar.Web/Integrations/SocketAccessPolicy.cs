using DeyeSolar.Web.Billing;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Integrations;

internal interface ISocketAccessPolicy
{
    Task EnsureAsync(CancellationToken ct, string? userId = null);
}

internal sealed class BillingSocketAccessPolicy(IDbContextFactory<DeyeSolarDbContext> factory, IBillingAccessReader billing) : ISocketAccessPolicy
{
    public async Task EnsureAsync(CancellationToken ct, string? userId = null)
    {
        if (userId is not null && !(await billing.ReadAsync(userId, ct)).HasAccess) throw new BillingAccessException();
        await using var db = await factory.CreateDbContextAsync(ct);
        await billing.EnsureInstallationAsync(db.InstallationId ?? throw new InvalidOperationException("An installation is required."), ct);
    }
}

// Direct gateway constructors are retained for integration fixtures. Production tenant composition always supplies billing.
internal sealed class UnrestrictedSocketAccessForTests : ISocketAccessPolicy
{
    public static readonly UnrestrictedSocketAccessForTests Instance = new();
    private UnrestrictedSocketAccessForTests() { }
    public Task EnsureAsync(CancellationToken ct, string? userId = null) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
}
