using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace DeyeSolar.Web.Services;

public interface IExportPriceStore
{
    Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
    Task SaveAsync(IReadOnlyList<ExportPriceInterval> prices, DateTimeOffset retrievedAt, CancellationToken ct);
}

public interface IScopedExportPriceStore
{
    Task<IReadOnlyList<ExportPriceInterval>> ReadFeedAsync(string sourceKey, DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
    Task SaveFeedAsync(string sourceKey, IReadOnlyList<ExportPriceInterval> prices, DateTimeOffset retrievedAt, CancellationToken ct);
}

public sealed class ExportPriceStore(IDbContextFactory<DeyeSolarDbContext> factory, TimeProvider clock) : IExportPriceStore, IScopedExportPriceStore
{
    public async Task<IReadOnlyList<ExportPriceInterval>> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (end <= start || end - start > TimeSpan.FromDays(367)) throw new ArgumentOutOfRangeException(nameof(end));
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ExportPrices.AsNoTracking().Where(row => row.StartUtc >= start.UtcDateTime && row.StartUtc < end.UtcDateTime)
            .OrderBy(row => row.StartUtc).ToListAsync(ct);
        return rows.Select(row =>
        {
            var from = new DateTimeOffset(DateTime.SpecifyKind(row.StartUtc, DateTimeKind.Utc));
            return new ExportPriceInterval(from, from.AddMinutes(15), row.PricePlnPerMwh);
        }).ToArray();
    }

    public async Task SaveAsync(IReadOnlyList<ExportPriceInterval> prices, DateTimeOffset retrievedAt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (prices.Count > 1000 || retrievedAt > now || retrievedAt < DateTimeOffset.FromUnixTimeSeconds(946684800))
            throw new ArgumentOutOfRangeException(nameof(retrievedAt));
        if (prices.Any(price => price.Start < DateTimeOffset.FromUnixTimeSeconds(946684800)
            || price.Start.UtcTicks % (TimeSpan.TicksPerMinute * 15) != 0
            || price.End != price.Start.AddMinutes(15)
            || price.PricePlnPerMwh != decimal.Round(price.PricePlnPerMwh, 6)
            || Math.Abs(price.PricePlnPerMwh) >= 1_000_000_000_000m)
            || prices.GroupBy(price => price.Start).Any(group => group.Select(price => price.PricePlnPerMwh).Distinct().Count() > 1))
            throw new ArgumentException("Prices require unique quarter-hour intervals and exact supported decimal precision.", nameof(prices));
        if (prices.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var price in prices.DistinctBy(price => price.Start).OrderBy(price => price.Start))
        {
            var amount = new SqlParameter("exportPrice", SqlDbType.Decimal)
            { Precision = 18, Scale = 6, Value = price.PricePlnPerMwh };
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @Amount decimal(18,6) = {amount};
                IF EXISTS (SELECT 1 FROM [ExportPrices] WITH (UPDLOCK, HOLDLOCK) WHERE [StartUtc] = {price.Start.UtcDateTime})
                    UPDATE [ExportPrices] SET [PricePlnPerMwh] = @Amount, [RetrievedAtUtc] = {retrievedAt.UtcDateTime}
                    WHERE [StartUtc] = {price.Start.UtcDateTime} AND [RetrievedAtUtc] <= {retrievedAt.UtcDateTime};
                ELSE
                    INSERT INTO [ExportPrices] ([StartUtc], [PricePlnPerMwh], [RetrievedAtUtc])
                    VALUES ({price.Start.UtcDateTime}, @Amount, {retrievedAt.UtcDateTime});
                """, ct);
        }
        await transaction.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<ExportPriceInterval>> ReadFeedAsync(string sourceKey, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (sourceKey.Length != 64 || sourceKey.Any(ch => !Uri.IsHexDigit(ch)) || end <= start || end - start > TimeSpan.FromDays(367))
            throw new ArgumentException("Invalid scoped feed price range.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ExportFeedPrices.AsNoTracking().Where(row => row.SourceKey == sourceKey && row.StartUtc >= start.UtcDateTime && row.StartUtc < end.UtcDateTime)
            .OrderBy(row => row.StartUtc).ToListAsync(ct);
        return rows.Select(row => new ExportPriceInterval(new DateTimeOffset(DateTime.SpecifyKind(row.StartUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(row.StartUtc, DateTimeKind.Utc)).AddMinutes(15), row.PricePlnPerMwh)).ToArray();
    }
    public async Task SaveFeedAsync(string sourceKey, IReadOnlyList<ExportPriceInterval> prices, DateTimeOffset retrievedAt, CancellationToken ct)
    {
        if (sourceKey.Length != 64 || sourceKey.Any(ch => !Uri.IsHexDigit(ch)) || prices.Count > 1000
            || retrievedAt > clock.GetUtcNow() || retrievedAt < DateTimeOffset.FromUnixTimeSeconds(946684800)
            || prices.Any(p => p.Start.Year < 2000 || p.Start.UtcTicks % TimeSpan.FromMinutes(15).Ticks != 0
                || p.End != p.Start.AddMinutes(15) || p.PricePlnPerMwh != decimal.Round(p.PricePlnPerMwh, 6)
                || Math.Abs(p.PricePlnPerMwh) >= 1_000_000_000_000m)
            || prices.GroupBy(p => p.Start).Any(g => g.Select(p => p.PricePlnPerMwh).Distinct().Count() != 1))
            throw new ArgumentException("Invalid scoped feed price batch.");
        if (prices.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var installation = db.InstallationId ?? throw new InvalidOperationException("Feed prices require an installation.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var price in prices.DistinctBy(p => p.Start).OrderBy(p => p.Start))
        {
            var amount = new SqlParameter("feedPrice", SqlDbType.Decimal) { Precision = 18, Scale = 6, Value = price.PricePlnPerMwh };
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @Amount decimal(18,6) = {amount};
                IF EXISTS (SELECT 1 FROM [ExportFeedPrices] WITH (UPDLOCK, HOLDLOCK) WHERE [InstallationId] = {installation} AND [SourceKey] = {sourceKey} AND [StartUtc] = {price.Start.UtcDateTime})
                    UPDATE [ExportFeedPrices] SET [PricePlnPerMwh] = @Amount, [RetrievedAtUtc] = {retrievedAt.UtcDateTime}
                    WHERE [InstallationId] = {installation} AND [SourceKey] = {sourceKey} AND [StartUtc] = {price.Start.UtcDateTime} AND [RetrievedAtUtc] <= {retrievedAt.UtcDateTime};
                ELSE
                    INSERT INTO [ExportFeedPrices] ([InstallationId], [SourceKey], [StartUtc], [PricePlnPerMwh], [RetrievedAtUtc])
                    VALUES ({installation}, {sourceKey}, {price.Start.UtcDateTime}, @Amount, {retrievedAt.UtcDateTime});
                """, ct);
        }
        await transaction.CommitAsync(ct);
    }

}
