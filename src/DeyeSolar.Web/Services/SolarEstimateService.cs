using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Services;

/// <summary>Fetch weather every ten minutes; evaluate the current minute and recent Deye measurement separately.</summary>
public sealed class SolarEstimateService(ISolarRadiationSource source, ISolarEstimateStore store,
    IOptionsMonitor<SolarEstimateOptions> options, TimeProvider clock, ILogger<SolarEstimateService> logger,
    IOptionsMonitor<DeyeCloudOptions> deyeOptions)
{
    private SolarEstimateState _current = SolarEstimateState.Empty;
    private CachedSolarObservation? _cached;
    private DateTimeOffset _nextFetch = DateTimeOffset.MinValue;
    private bool _loaded;
    private bool _refreshFailed;
    private string? _error;
    public SolarEstimateState Current => Volatile.Read(ref _current);
    public event Action? OnUpdated;

    internal void Reset(string? reason = null)
    {
        _cached = null;
        _nextFetch = DateTimeOffset.MinValue;
        _loaded = false;
        _refreshFailed = false;
        _error = null;
        Publish(reason is null ? SolarEstimateState.Empty : SolarEstimateState.Empty with
        {
            Error = reason,
            Comparison = SolarEstimateState.Empty.Comparison with { Reason = reason }
        });
    }

    internal async Task RunScheduledUpdateAsync(CancellationToken ct)
    {
        try { await UpdateAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Solar estimate cycle unavailable ({ErrorType})", exception.GetType().Name);
            Publish(Current with { RefreshFailed = true, Error = "The estimate could not be refreshed.",
                Comparison = new(SolarComparisonStatus.InsufficientData, null, null, null, "A reliable comparison is unavailable.") });
        }
    }

    internal async Task UpdateAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var config = options.CurrentValue;
        config.Validate();
        var key = ConfigurationKey(config);
        if (!_loaded)
        {
            _loaded = true;
            try { _cached = await store.LoadAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning("Solar cache could not be loaded ({ErrorType})", ex.GetType().Name); }
        }
        if (_cached != null && (_cached.ConfigurationKey != key || _cached.Observation.Timestamp > now || _cached.RetrievedAt > now))
        {
            _cached = null;
            _nextFetch = DateTimeOffset.MinValue;
        }

        if (now >= _nextFetch)
        {
            _nextFetch = now.AddMinutes(10);
            try
            {
                var fetched = await source.ReadAsync(config, now, ct);
                // Validate before replacing the last good observation. Never regress to older data.
                _ = SolarPowerCalculator.Calculate(fetched, config, now);
                if (_cached != null && fetched.Timestamp < _cached.Observation.Timestamp)
                    throw new InvalidDataException("Solar weather source regressed.");
                _cached = new(key, fetched, clock.GetUtcNow());
                _refreshFailed = false;
                _error = null;
                try { await store.SaveAsync(_cached, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Solar cache could not be persisted ({ErrorType})", ex.GetType().Name);
                    _error = "The estimate is available but could not be saved for a server restart.";
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _refreshFailed = true;
                _error = "The weather API is unavailable or returned incomplete data. The last successful result is retained.";
                // Do not log exception messages/URLs: customer API keys can be query parameters.
                logger.LogWarning("Solar radiation refresh failed ({ErrorType})", ex.GetType().Name);
            }
        }

        if (_cached == null)
        {
            Publish(SolarEstimateState.Empty with { RefreshFailed = _refreshFailed, Error = _error });
            return;
        }

        now = clock.GetUtcNow();
        var observation = _cached.Observation;
        var isModel = observation.Kind == SolarRadiationKind.WeatherModel;
        if (isModel)
        {
            observation = observation with { RetrievedAt = _cached.RetrievedAt };
            try
            {
                if ((now - _cached.RetrievedAt).TotalMinutes > config.NowcastMaximumAgeMinutes)
                    throw new InvalidDataException("Cached weather is too old for a current estimate.");
                observation = SolarNowcast.Interpolate(observation, now);
            }
            catch (InvalidDataException)
            {
                // Never advance the timestamp of a frozen or extrapolated value and label it 'now'.
                Publish(SolarEstimateState.Empty with { RefreshFailed = true, LastSuccessAt = _cached.RetrievedAt,
                    Error = "Fresh weather data for the current minute is unavailable." });
                return;
            }
        }
        var estimate = SolarPowerCalculator.Calculate(observation, config, now);
        SolarActual? actual = null;
        try { actual = await store.FindActualAsync(estimate.Timestamp,
            isModel ? config.NowcastActualMaximumAgeMinutes * 60 : config.AlignmentToleranceSeconds, now, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning("Solar comparison history unavailable ({ErrorType})", ex.GetType().Name); }
        SolarPowerEstimate? comparisonEstimate = null;
        if (isModel && actual != null && actual.Timestamp <= now
            && (now - actual.Timestamp).TotalMinutes <= config.NowcastActualMaximumAgeMinutes)
        {
            try { comparisonEstimate = SolarPowerCalculator.Calculate(SolarNowcast.Interpolate(observation, actual.Timestamp), config, now); }
            catch (InvalidDataException) { /* No model bracket for this Deye measurement. */ }
        }
        var comparison = isModel && comparisonEstimate is null
            ? new SolarComparison(SolarComparisonStatus.InsufficientData, actual, null, null,
                "No recent Deye reading has weather data for its measurement time.")
            : SolarPowerCalculator.Compare(comparisonEstimate ?? estimate, actual, config, now, _refreshFailed);
        if (!config.DeyeSolarPowerIsPvDcConfirmed || string.IsNullOrWhiteSpace(config.DeyeConfirmedDeviceSn)
            || config.DeyeConfirmedDeviceSn != deyeOptions.CurrentValue.DeviceSn)
            comparison = new(SolarComparisonStatus.InsufficientData, actual, null, null,
                "Confirm that Deye TotalSolarPower is the total PV DC power of this installation.");
        Publish(new(estimate, comparison, _refreshFailed, _cached.RetrievedAt, _error) { ComparisonEstimate = comparisonEstimate });
    }

    private void Publish(SolarEstimateState state)
    {
        Volatile.Write(ref _current, state);
        if (OnUpdated is not { } updated) return;
        foreach (Action subscriber in updated.GetInvocationList())
        {
            try { subscriber(); }
            catch (Exception ex) { logger.LogDebug("Solar card subscriber disconnected ({ErrorType})", ex.GetType().Name); }
        }
    }

    internal static string ConfigurationKey(SolarEstimateOptions config)
    {
        var values = typeof(SolarEstimateOptions).GetProperties().Where(p => p.Name != nameof(config.ApiKey))
            .OrderBy(p => p.Name).ToDictionary(p => p.Name, p => p.GetValue(config));
        return "weather-now-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    }
}
