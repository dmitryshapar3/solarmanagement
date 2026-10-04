using System.Security.Cryptography;
using DeyeSolar.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Workers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Billing;
using SolarManagement.Inverters.Contracts;

namespace DeyeSolar.Web.Tenancy;

public sealed class TenantRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IConfigurationRoot _configuration;
    private readonly TenantRuntimeLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly ILogger<TenantRuntime> _logger;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _sync = new();
    private readonly IDisposable _configurationSubscription;
    private readonly IntegrationChangeNotifier _integrationChanges;
    private CancellationTokenSource? _cycleCancellation;
    private Task? _disposeTask;
    private string _deyeKey;
    private string _shellyKey;
    private string _siteKey;
    private int _solarReset;
    private int _bootstrapRequested;
    private DateTimeOffset _nextPoll;
    private DateTimeOffset _nextDevices;
    private DateTimeOffset _nextSolar;

    internal TenantRuntime(string id, ServiceProvider provider, IConfigurationRoot configuration,
        TenantRuntimeLifetime lifetime, TimeProvider clock)
    {
        InstallationId = id;
        _provider = provider;
        _configuration = configuration;
        _lifetime = lifetime;
        _clock = clock;
        _logger = provider.GetRequiredService<ILogger<TenantRuntime>>();
        _integrationChanges = provider.GetRequiredService<IntegrationChangeNotifier>();
        _integrationChanges.Changed += IntegrationChanged;
        (_deyeKey, _shellyKey, _siteKey) = ConfigurationKeys();
        if (!TenantRuntimeOptions.HasSolarConfiguration(_configuration.GetSection(SolarEstimateOptions.Section).Get<SolarEstimateOptions>()!))
            Resolve<SolarEstimateService>().Reset(TenantRuntimeOptions.ConfigureSiteMessage);
        _configurationSubscription = ChangeToken.OnChange(configuration.GetReloadToken, ConfigurationChanged);
    }

    public string InstallationId { get; }
    private void IntegrationChanged(string installationId, Guid instanceId)
    {
        if (installationId.Length == 0)
        {
            Interlocked.Exchange(ref _bootstrapRequested, 1);
            return;
        }
        if (installationId != InstallationId) return;
        CancellationTokenSource? operation;
        lock (_sync)
        {
            if (_disposeTask is not null) return;
            operation = _cycleCancellation;
            _solarReset = 1;
            _nextPoll = _nextDevices = _nextSolar = DateTimeOffset.MinValue;
        }
        Resolve<InverterSelectionMonitor>().Invalidate();
        Resolve<DynamicSocketGateway>().Invalidate();
        Cancel(operation);
        Notify(() => Resolve<InverterDataSnapshot>().Clear());
        Notify(() => Resolve<DeviceStatusSnapshot>().Clear());
        _ = ResetEstimateAsync();
    }
    public IServiceProvider Services => _provider;
    public T Resolve<T>() where T : notnull
    {
        lock (_sync) ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        return _provider.GetRequiredService<T>();
    }

    public async Task RefreshSettingsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync) ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        if (Interlocked.Exchange(ref _bootstrapRequested, 0) != 0 && _provider.GetService<LegacyIntegrationBootstrap>() is { } bootstrap)
            await bootstrap.RunAsync(Resolve<IDbContextFactory<DeyeSolarDbContext>>(), _configuration, ct);
        _configuration.Reload();
    }

    private (string Deye, string Shelly, string Site) ConfigurationKeys() =>
        // Read provider values directly: callback ordering must not rely on OptionsMonitor invalidation.
        (Key(_configuration.GetSection(DeyeCloudOptions.Section).Get<DeyeCloudOptions>()!),
         Key(_configuration.GetSection(ShellyOptions.Section).Get<ShellyOptions>()!),
         Key(new { Estimate = _configuration.GetSection(SolarEstimateOptions.Section).Get<SolarEstimateOptions>(),
             Sales = _configuration.GetSection(SolarSalesOptions.Section).Get<SolarSalesOptions>() }));
    private static string Key(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private void ConfigurationChanged()
    {
        CancellationTokenSource? operation;
        bool clearDeye, clearShelly;
        var keys = ConfigurationKeys();
        lock (_sync)
        {
            if (_disposeTask is not null) return;
            clearDeye = keys.Deye != _deyeKey;
            clearShelly = keys.Shelly != _shellyKey;
            var siteChanged = keys.Site != _siteKey;
            if (!clearDeye && !clearShelly && !siteChanged) return;
            _deyeKey = keys.Deye;
            _shellyKey = keys.Shelly;
            _siteKey = keys.Site;
            _solarReset = 1;
            _nextPoll = _nextDevices = _nextSolar = DateTimeOffset.MinValue;
            operation = _cycleCancellation;
        }
        // Fence in-flight readers before clearing snapshots, even if our reload callback ran first.
        if (clearDeye) _provider.GetRequiredService<IOptionsMonitorCache<DeyeCloudOptions>>().Clear();
        if (clearShelly) _provider.GetRequiredService<IOptionsMonitorCache<ShellyOptions>>().Clear();
        _provider.GetRequiredService<IOptionsMonitorCache<SolarEstimateOptions>>().Clear();
        _provider.GetRequiredService<IOptionsMonitorCache<SolarSalesOptions>>().Clear();
        Cancel(operation);
        if (clearDeye) Notify(() => Resolve<InverterDataSnapshot>().Clear());
        if (clearShelly) Notify(() => Resolve<DeviceStatusSnapshot>().Clear());
        // The serialized cycle applies Reset after any canceled update has left the estimate service.
        _ = ResetEstimateAsync();
    }

    private async Task ResetEstimateAsync()
    {
        try
        {
            await _cycleGate.WaitAsync(_lifetime.ApplicationStopping).ConfigureAwait(false);
            try { ApplySolarReset(); }
            finally { _cycleGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { _logger.LogWarning("Tenant estimate reset unavailable ({ErrorType})", exception.GetType().Name); }
    }
    private void ApplySolarReset()
    {
        if (Interlocked.Exchange(ref _solarReset, 0) != 0) Notify(() => Resolve<SolarEstimateService>().Reset(
            TenantRuntimeOptions.HasSolarConfiguration(_configuration.GetSection(SolarEstimateOptions.Section).Get<SolarEstimateOptions>()!)
                ? null : TenantRuntimeOptions.ConfigureSiteMessage));
    }

    internal async Task RunDueWorkAsync(CancellationToken ct)
    {
        if (!await _cycleGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        CancellationTokenSource operation;
        lock (_sync)
        {
            if (_disposeTask is not null) { _cycleGate.Release(); return; }
            operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.ApplicationStopping);
            _cycleCancellation = operation;
        }
        try
        {
            ApplySolarReset();
            if (_provider.GetService<BillingAccessService>() is { } billing
                && !await billing.InstallationHasAccessAsync(InstallationId, operation.Token).ConfigureAwait(false))
            {
                Resolve<DeviceStatusSnapshot>().Clear();
                return;
            }
            await Resolve<InverterSelectionMonitor>().RefreshAsync(operation.Token).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var interval = TimeSpan.FromSeconds(Math.Clamp(Resolve<IOptionsMonitor<PollingOptions>>().CurrentValue.IntervalSeconds, 1, 3600));
            if (now >= _nextPoll && !string.IsNullOrWhiteSpace(Resolve<IOptionsMonitor<InverterConnectionOptions>>().CurrentValue.DeviceKey))
            {
                _nextPoll = now + interval;
                await RunSafelyAsync(() => Resolve<PollingWorker>().PollAndEvaluateAsync(operation.Token), "Inverter", operation.Token).ConfigureAwait(false);
            }
            if (now >= _nextDevices)
            {
                _nextDevices = now + interval;
                await RunSafelyAsync(async () =>
                {
                    var snapshot = Resolve<DeviceStatusSnapshot>();
                    var epoch = snapshot.Epoch;
                    var devices = await Resolve<ISocketInventoryService>().RefreshDevicesAsync(operation.Token).ConfigureAwait(false);
                    operation.Token.ThrowIfCancellationRequested();
                    snapshot.TryUpdate(devices, epoch);
                }, "Socket discovery", operation.Token).ConfigureAwait(false);
            }
            if (now >= _nextSolar && TenantRuntimeOptions.HasSolarConfiguration(Resolve<IOptionsMonitor<SolarEstimateOptions>>().CurrentValue))
            {
                _nextSolar = now.AddMinutes(1);
                await RunSafelyAsync(() => Resolve<SolarEstimateService>().RunScheduledUpdateAsync(operation.Token), "Solar estimate", operation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally
        {
            lock (_sync) if (ReferenceEquals(_cycleCancellation, operation)) _cycleCancellation = null;
            operation.Dispose();
            _cycleGate.Release();
        }
    }

    private async Task RunSafelyAsync(Func<Task> work, string name, CancellationToken ct)
    {
        try { await work().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { _logger.LogWarning("Tenant {TaskName} cycle unavailable ({ErrorType})", name, exception.GetType().Name); }
    }
    private void Notify(Action notification)
    {
        try { notification(); }
        catch (Exception exception) { _logger.LogWarning("Tenant snapshot notification unavailable ({ErrorType})", exception.GetType().Name); }
    }
    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposeTask ??= StopAsync());
    }
    private async Task StopAsync()
    {
        _configurationSubscription.Dispose();
        _integrationChanges.Changed -= IntegrationChanged;
        _lifetime.StopApplication();
        Cancel(_cycleCancellation);
        await _cycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Notify(() => _provider.GetRequiredService<InverterDataSnapshot>().Clear());
            Notify(() => _provider.GetRequiredService<DeviceStatusSnapshot>().Clear());
            await _provider.DisposeAsync().ConfigureAwait(false);
            (_configuration as IDisposable)?.Dispose();
            _lifetime.Dispose();
        }
        finally { _cycleGate.Release(); }
    }
}
