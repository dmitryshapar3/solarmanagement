using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using SolarManagement.Integrations.Contracts;
using SolarManagement.Inverters.Contracts;
using InverterSolarBasis = SolarManagement.Inverters.Contracts.SolarPowerBasis;

namespace DeyeSolar.Web.Integrations;

public sealed class DynamicInverterGateway(IIntegrationRegistry registry, IIntegrationRuntimeExecutor executor,
    InverterSelectionMonitor selection, TimeProvider clock) : IInverterCatalog, IInverterDataSource,
    IExportGridHistorySource, IInverterSelectionRefresher, IRegisteredInverterDataSource
{
    private readonly TimeProvider _clock = clock;
    public Task RefreshSelectionAsync(CancellationToken ct) => selection.RefreshAsync(ct);
    public async Task<IReadOnlyList<InverterDescriptor>> ListRegisteredAsync(CancellationToken ct)
        => (await registry.ListBindingsAsync(ct)).Where(b => b.Kind == "inverter" && b.Enabled)
            .Select(b => new InverterDescriptor(new(b.Id), b.Name, IntegrationCapabilities.Read(b))).ToArray();
    public async Task<IInverter> GetAsync(InverterId id, CancellationToken ct)
    {
        var binding = await registry.FindBindingAsync(id.Value, ct);
        if (binding is not { Enabled: true, Kind: "inverter" })
            throw new InvalidOperationException("Choose an available inverter from this installation.");
        var session = await registry.GetRuntimeSessionAsync(binding.InstanceId, ct);
        return new Inverter(this, binding, session);
    }
    public async Task<IInverterGridHistory?> GetGridHistoryAsync(InverterId id, CancellationToken ct)
    {
        var inverter = (Inverter)await GetAsync(id, ct);
        return inverter.Capabilities.HasGridPowerHistory ? inverter : null;
    }
    public async Task<InverterData> ReadCurrentDataAsync(CancellationToken ct)
    {
        await selection.RefreshAsync(ct);
        if (!Guid.TryParse(selection.CurrentValue.DeviceKey, out var id))
            throw new InvalidOperationException("Select an inverter in Integrations.");
        return await ReadDeviceAsync(new(id), ct);
    }
    public async Task<InverterData> ReadDeviceAsync(InverterId deviceId, CancellationToken ct)
    {
        var inverter = (Inverter)await GetAsync(deviceId, ct);
        var telemetry = await inverter.ReadAsync(ct);
        var id = deviceId.Value;
        var identity = inverter.Session;
        return new InverterData
        {
            Telemetry = telemetry,
            InverterId = id,
            ConfigurationRevision = identity.ConfigurationRevision,
            RuntimeGeneration = identity.Generation,
            BatterySoc = telemetry.BatterySoc.Value is { } soc ? decimal.ToInt32(soc.Value) : 0,
            BatterySocValid = telemetry.BatterySoc.Quality == MeasurementQuality.Good,
            BatteryPower = telemetry.BatteryPower.Value?.Value ?? 0,
            BatteryTemperature = telemetry.BatteryTemperature.Value?.Value ?? 0,
            BatteryVoltage = telemetry.BatteryVoltage.Value?.Value ?? 0,
            BatteryCurrent = telemetry.BatteryCurrent.Value?.Value ?? 0,
            SolarProduction = telemetry.SolarPower.Value?.Value ?? 0,
            SolarObservedAt = telemetry.SolarPower.Quality == MeasurementQuality.Good ? telemetry.SolarPower.ObservedAt : null,
            SolarDeviceSn = telemetry.SolarPower.Quality == MeasurementQuality.Good ? id.ToString("D") : null,
            GridConsumption = telemetry.GridPower.Value?.Value ?? 0,
            GridObservedAt = telemetry.GridPower.Quality == MeasurementQuality.Good ? telemetry.GridPower.ObservedAt : null,
            GridDeviceSn = telemetry.GridPower.Quality == MeasurementQuality.Good ? id.ToString("D") : null,
            LoadPower = telemetry.LoadPower.Value?.Value ?? 0,
            Timestamp = telemetry.ReceivedAt
        };
    }
    public async Task<bool> IsCurrentAsync(InverterData data, CancellationToken ct)
    {
        if (data.InverterId is not { } id) return false;
        var binding = await registry.FindBindingAsync(id, ct);
        if (binding is not { Enabled: true, Kind: "inverter" }) return false;
        var snapshot = await registry.GetSnapshotAsync(binding.InstanceId, ct);
        return snapshot?.Instance is { Status: "enabled" } instance
            && instance.Revision == data.ConfigurationRevision && instance.Generation == data.RuntimeGeneration;
    }
    public async Task<IReadOnlyList<ExportGridSample>> ReadAsync(string deviceSn, DateTimeOffset start,
        DateTimeOffset end, CancellationToken ct)
    {
        if (!Guid.TryParse(deviceSn, out var id)) throw new ArgumentException("A registered inverter identity is required.");
        var history = (Inverter?)await GetGridHistoryAsync(new(id), ct)
            ?? throw new NotSupportedException("This inverter does not provide grid history.");
        var results = new List<ExportGridSample>();
        string? continuation = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await history.ReadGridHistoryAsync(new(start, end, continuation), ct);
            var session = history.Session;
            results.AddRange(page.Samples.Select(s => new ExportGridSample(s.ObservedAt, s.Power.Value,
                id, session.ConfigurationRevision, session.Generation)));
            if (results.Count > 300) throw new InvalidDataException("History exceeded the allowed batch size.");
            continuation = page.ContinuationToken;
            if (continuation is not null && (!seen.Add(continuation) || seen.Count > 100))
                throw new InvalidDataException("History pagination did not advance.");
            if (continuation is null && !page.IsComplete) throw new InvalidDataException("History is incomplete.");
        } while (continuation is not null);
        return results;
    }
    private async Task<JsonElement> InvokeAsync(IntegrationDeviceBindingEntity binding, IntegrationSession original,
        string method, object parameters, CancellationToken ct)
    {
        var current = await registry.GetRuntimeSessionAsync(binding.InstanceId, ct);
        if (current.ConfigurationRevision != original.ConfigurationRevision || current.Generation != original.Generation)
            throw new InvalidOperationException("The inverter connection changed. Refresh and try again.");
        var response = await executor.InvokeAsync(current, method, IntegrationJson.Element(parameters), ct);
        var latest = await registry.GetSnapshotAsync(binding.InstanceId, ct);
        if (latest?.Instance.Status != "enabled" || latest.Instance.Revision != current.ConfigurationRevision
            || latest.Instance.Generation != current.Generation)
            throw new InvalidOperationException("The inverter connection changed during the request.");
        return response;
    }
    private Measurement<T> Measure<T>(ProviderMeasurement? data, Func<decimal, T> create,
        decimal minimum, decimal maximum) where T : struct
    {
        if (data is null) return new(null, null, MeasurementQuality.Missing);
        var quality = (MeasurementQuality)data.Quality;
        if (!Enum.IsDefined(quality)) return new(null, data.ObservedAt, MeasurementQuality.Invalid);
        if (quality != MeasurementQuality.Good) return new(null, data.ObservedAt, quality);
        if (data.Value is null) return new(null, data.ObservedAt, MeasurementQuality.Missing);
        if (data.Value < minimum || data.Value > maximum || data.ObservedAt is not { } time
            || time < DateTimeOffset.FromUnixTimeSeconds(946684800) || time > _clock.GetUtcNow())
            return new(null, data.ObservedAt, MeasurementQuality.Invalid);
        if (_clock.GetUtcNow() - time > TimeSpan.FromMinutes(10))
            return new(create(data.Value.Value), time, MeasurementQuality.Stale);
        return new(create(data.Value.Value), data.ObservedAt, MeasurementQuality.Good);
    }
    private sealed class Inverter(DynamicInverterGateway owner, IntegrationDeviceBindingEntity binding,
        IntegrationSession session) : IInverter, IInverterGridHistory
    {
        public IntegrationSession Session => session;
        public InverterId Id => new(binding.Id);
        public InverterCapabilities Capabilities { get; } = IntegrationCapabilities.Read(binding);
        public async Task<IInverterTelemetry> ReadAsync(CancellationToken ct)
        {
            var json = await owner.InvokeAsync(binding, session, "inverter.read", new { remoteId = binding.RemoteId, channel = binding.Channel }, ct);
            var data = json.Deserialize<ProviderInverterTelemetry>(IntegrationJson.Options) ?? throw new InvalidDataException("Missing telemetry.");
            if (data.RemoteId != binding.RemoteId) throw new InvalidDataException("Telemetry belongs to another inverter.");
            var basis = Enum.TryParse<InverterSolarBasis>(data.SolarBasis, out var parsed) && Enum.IsDefined(parsed)
                ? parsed : InverterSolarBasis.Unknown;
            return new InverterTelemetry(Id, owner._clock.GetUtcNow(),
                owner.Measure(data.BatterySoc, v => new Percent(v), 0, 100),
                owner.Measure(data.BatteryPower, v => new Watts(decimal.ToInt32(v)), int.MinValue, int.MaxValue),
                owner.Measure(data.BatteryTemperature, v => new Celsius((double)v), -273.15m, 1000),
                owner.Measure(data.BatteryVoltage, v => new Volts((double)v), 0, 100000),
                owner.Measure(data.BatteryCurrent, v => new Amperes((double)v), -100000, 100000),
                owner.Measure(data.SolarPower, v => new Watts(decimal.ToInt32(v)), 0, int.MaxValue),
                owner.Measure(data.GridPower, v => new Watts(decimal.ToInt32(v)), int.MinValue, int.MaxValue),
                owner.Measure(data.LoadPower, v => new Watts(decimal.ToInt32(v)), 0, int.MaxValue), basis);
        }
        public async Task<GridHistoryPage> ReadGridHistoryAsync(GridHistoryQuery query, CancellationToken ct)
        {
            if (!Capabilities.HasGridPowerHistory) throw new NotSupportedException("Grid history is unsupported.");
            if (query.ToExclusive <= query.FromInclusive || query.ToExclusive - query.FromInclusive > TimeSpan.FromDays(1)
                || query.ToExclusive > owner._clock.GetUtcNow() || query.ContinuationToken?.Length > 4096)
                throw new ArgumentOutOfRangeException(nameof(query));
            var json = await owner.InvokeAsync(binding, session, "inverter.history", new
            {
                remoteId = binding.RemoteId,
                start = query.FromInclusive,
                end = query.ToExclusive,
                continuationToken = query.ContinuationToken
            }, ct);
            var data = json.Deserialize<ProviderGridHistory>(IntegrationJson.Options) ?? throw new InvalidDataException("Missing history.");
            if (data.RemoteId != binding.RemoteId || data.Samples.Count > 300
                || data.Samples.Any(s => s.ObservedAt < query.FromInclusive || s.ObservedAt >= query.ToExclusive))
                throw new InvalidDataException("History has invalid provenance or timestamps.");
            return new(Id, data.Samples.Select(s => new GridPowerSample(s.ObservedAt, new(s.PowerWatts))).ToArray(),
                data.ContinuationToken, data.IsComplete);
        }
    }
}
