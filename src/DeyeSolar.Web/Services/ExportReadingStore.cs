using DeyeSolar.Domain.Models;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DeyeSolar.Web.Services;

public interface IExportReadingStore
{
    Task<IReadOnlyList<ExportGridSample>> ReadAsync(string deviceSn, DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct);
    Task UpsertHistoryAsync(string deviceSn, IReadOnlyList<ExportGridSample> samples,
        DateTimeOffset polledAt, CancellationToken ct);
}

public sealed class ExportReadingStore(IDbContextFactory<DeyeSolarDbContext> factory, TimeProvider clock) : IExportReadingStore
{
    public async Task<IReadOnlyList<ExportGridSample>> ReadAsync(string deviceSn, DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateDevice(deviceSn);
        if (end <= start || end - start > TimeSpan.FromDays(367))
            throw new ArgumentOutOfRangeException(nameof(end));
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.ExportReadings.AsNoTracking().Where(row => row.DeviceSn == deviceSn
            && row.ObservedAt >= start.UtcDateTime && row.ObservedAt < end.UtcDateTime)
            .OrderBy(row => row.ObservedAt).ToListAsync(ct);
        return rows.Select(row => new ExportGridSample(
            new DateTimeOffset(DateTime.SpecifyKind(row.ObservedAt, DateTimeKind.Utc)), row.GridPowerWatts)).ToArray();
    }

    public async Task UpsertHistoryAsync(string deviceSn, IReadOnlyList<ExportGridSample> samples,
        DateTimeOffset polledAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateDevice(deviceSn);
        if (samples.Count > 300) throw new ArgumentOutOfRangeException(nameof(samples));
        ValidateTime(polledAt, clock.GetUtcNow(), nameof(polledAt));
        foreach (var sample in samples) ValidateTime(sample.Timestamp, polledAt, nameof(samples));
        if (samples.GroupBy(sample => sample.Timestamp).Any(group => group.Select(sample => sample.GridPowerWatts).Distinct().Count() > 1))
            throw new ArgumentException("A history batch contains conflicting measurements.", nameof(samples));
        if (samples.Count == 0) return;

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var identity = samples[0];
        if (identity.InverterId is { } deviceId)
        {
            if (deviceSn != deviceId.ToString("D") || samples.Any(s => s.InverterId != deviceId
                || s.ConfigurationRevision != identity.ConfigurationRevision || s.RuntimeGeneration != identity.RuntimeGeneration))
                throw new InvalidDataException("A history batch contains multiple source generations.");
            if (!await IntegrationPersistenceGuard.LockCurrentAsync(db, deviceId, identity.ConfigurationRevision, identity.RuntimeGeneration, ct))
                throw new InvalidOperationException("The inverter connection changed before history was saved.");
        }
        // Stable lock order also applies when overlapping backfill batches run concurrently.
        foreach (var sample in samples.DistinctBy(sample => sample.Timestamp).OrderBy(sample => sample.Timestamp))
            await UpsertAsync(db, deviceSn, sample.Timestamp.UtcDateTime, sample.GridPowerWatts, polledAt.UtcDateTime, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task SavePollingAsync(InverterData data, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var validGrid = data.GridObservedAt.HasValue && !string.IsNullOrWhiteSpace(data.GridDeviceSn);
        if (validGrid)
        {
            ValidateDevice(data.GridDeviceSn!);
            ValidateTime(data.Timestamp, now, nameof(data));
            ValidateTime(data.GridObservedAt!.Value, data.Timestamp, nameof(data));
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (data.InverterId is { } deviceId && !await IntegrationPersistenceGuard.LockCurrentAsync(db, deviceId,
            data.ConfigurationRevision, data.RuntimeGeneration, ct))
            throw new InvalidOperationException("The inverter connection changed before telemetry was saved.");
        if (validGrid)
            await UpsertAsync(db, data.GridDeviceSn!, data.GridObservedAt!.Value.UtcDateTime,
                data.GridConsumption, data.Timestamp.UtcDateTime, ct);
        db.Readings.Add(new Reading
        {
            Timestamp = now.UtcDateTime,
            BatterySoc = data.BatterySoc,
            BatteryTemperature = data.BatteryTemperature,
            BatteryVoltage = data.BatteryVoltage,
            BatteryPower = data.BatteryPower,
            BatteryCurrent = data.BatteryCurrent,
            SolarProduction = data.SolarProduction,
            SolarObservedAt = data.SolarObservedAt?.UtcDateTime,
            SolarDeviceSn = data.SolarObservedAt.HasValue ? data.SolarDeviceSn : null,
            GridConsumption = data.GridConsumption,
            LoadPower = data.LoadPower,
            InverterId = data.InverterId,
            BatterySocValid = data.BatterySocValid,
            ConfigurationRevision = data.ConfigurationRevision,
            RuntimeGeneration = data.RuntimeGeneration,
            DataSource = "Integration"
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static Task<int> UpsertAsync(DeyeSolarDbContext db, string deviceSn, DateTime observedAt,
        int watts, DateTime polledAt, CancellationToken ct)
    {
        var installationId = db.InstallationId ?? throw new InvalidOperationException("Private writes require an installation.");
        return db.Database.ExecuteSqlInterpolatedAsync($"""
        IF EXISTS (SELECT 1 FROM [ExportReadings] WITH (UPDLOCK, HOLDLOCK)
                   WHERE [InstallationId] = {installationId} AND [DeviceSn] = {deviceSn} AND [ObservedAt] = {observedAt})
            UPDATE [ExportReadings] SET [GridPowerWatts] = {watts}, [PolledAt] = {polledAt}
            WHERE [InstallationId] = {installationId} AND [DeviceSn] = {deviceSn} AND [ObservedAt] = {observedAt} AND [PolledAt] <= {polledAt};
        ELSE
            INSERT INTO [ExportReadings] ([InstallationId], [DeviceSn], [ObservedAt], [GridPowerWatts], [PolledAt])
            VALUES ({installationId}, {deviceSn}, {observedAt}, {watts}, {polledAt});
        """, ct);
    }

    private static void ValidateDevice(string deviceSn)
    {
        if (string.IsNullOrWhiteSpace(deviceSn) || deviceSn.Length > 128 || deviceSn != deviceSn.Trim())
            throw new ArgumentException("Grid readings require a device identity.", nameof(deviceSn));
    }

    private static void ValidateTime(DateTimeOffset timestamp, DateTimeOffset latest, string parameter)
    {
        if (timestamp < DateTimeOffset.FromUnixTimeSeconds(946684800) || timestamp > latest)
            throw new ArgumentOutOfRangeException(parameter, "Grid readings require a real nonfuture timestamp.");
    }
}
