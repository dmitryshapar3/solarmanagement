using System.IO.Compression;
using System.Text.Json;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Auth;

public sealed class AccountZipExporter(DbContextOptions<DeyeSolarDbContext> database, TimeProvider clock)
{
    private const int MaximumRows = 500_000;
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;
    public async Task<byte[]> ExportAsync(IdentityUser user, CancellationToken ct)
    {
        await using var db = new DeyeSolarDbContext(database);
        var memberships = await db.InstallationMemberships.AsNoTracking().Where(m => m.UserId == user.Id).ToListAsync(ct);
        var owned = memberships.Where(m => m.Role == "Owner").Select(m => m.InstallationId).ToArray();
        var readings = db.Readings.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId));
        var exports = db.ExportReadings.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId));
        var feedPrices = db.ExportFeedPrices.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId));
        var activity = db.ActivityEvents.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId));
        if ((long)await readings.CountAsync(ct) + await exports.CountAsync(ct) + await feedPrices.CountAsync(ct) + await activity.CountAsync(ct) > MaximumRows)
            throw new AccountSecurityException("export_too_large", "Contact support to export this account's full history.", 413);
        var claims = await db.UserClaims.AsNoTracking().Where(c => c.UserId == user.Id
            && (c.ClaimType == AccountManagementService.DisplayNameClaim || c.ClaimType == AccountManagementService.DisplayZoneClaim)).ToListAsync(ct);
        var settings = (await db.AppSettings.IgnoreQueryFilters().AsNoTracking().Where(s => owned.Contains(s.InstallationId)).ToListAsync(ct))
            .Where(s => SettingsSchema.IsRuntimeSetting(s.Section, s.Key)).Select(s => new { s.InstallationId, s.Section, s.Key, s.Value });
        using var memory = new MemoryStream();
        using (var bounded = new BoundedArchiveStream(memory, MaximumArchiveBytes))
        using (var archive = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteAsync(archive, "manifest.json", new { formatVersion = 1, exportedAt = clock.GetUtcNow(), accountId = user.Id,
                ownership = "Account data and installations owned by this account", datasets = new[] { "account", "memberships", "billing", "subscriptions", "installations", "settings", "rules", "rule-runs", "readings", "export-readings", "export-feed-prices", "activity" } }, ct);
            await WriteAsync(archive, "account.json", new { user.Id, user.UserName, user.Email, user.EmailConfirmed, user.PhoneNumber, user.PhoneNumberConfirmed,
                displayName = claims.FirstOrDefault(c => c.ClaimType == AccountManagementService.DisplayNameClaim)?.ClaimValue,
                displayTimeZoneId = claims.FirstOrDefault(c => c.ClaimType == AccountManagementService.DisplayZoneClaim)?.ClaimValue }, ct);
            await WriteAsync(archive, "memberships.json", memberships.Select(m => new { m.InstallationId, m.Role }), ct);
            await WriteAsync(archive, "billing.json", await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(b => b.UserId == user.Id, ct), ct);
            await WriteAsync(archive, "subscriptions.json", db.AppleSubscriptions.AsNoTracking().Where(s => s.UserId == user.Id).AsAsyncEnumerable(), ct);
            await WriteAsync(archive, "installations.json", db.Installations.AsNoTracking().Where(i => owned.Contains(i.Id)).AsAsyncEnumerable(), ct);
            await WriteAsync(archive, "settings.json", settings, ct);
            await WriteAsync(archive, "rules.json", db.TriggerRules.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId)).AsAsyncEnumerable(), ct);
            await WriteAsync(archive, "rule-runs.json", db.RuleRunLogs.IgnoreQueryFilters().AsNoTracking().Where(r => owned.Contains(r.InstallationId)).AsAsyncEnumerable(), ct);
            await WriteAsync(archive, "readings.json", readings.AsAsyncEnumerable(), ct);
            await WriteAsync(archive, "export-readings.json", exports.AsAsyncEnumerable(), ct);
            await WriteAsync(archive, "export-feed-prices.json", feedPrices.AsAsyncEnumerable(), ct);
            await WriteAsync(archive, "activity.json", activity.AsAsyncEnumerable(), ct);
        }
        return memory.ToArray();
    }
    private static async Task WriteAsync<T>(ZipArchive zip, string name, T data, CancellationToken ct)
    {
        await using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        await JsonSerializer.SerializeAsync(stream, data, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
    }
    private sealed class BoundedArchiveStream(Stream inner, long limit) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); inner.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return inner.WriteAsync(buffer, ct); }
        private void Check(int count) { if (_written + count > limit) throw new AccountSecurityException("export_too_large", "Contact support to export this account's full history.", 413); _written += count; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
