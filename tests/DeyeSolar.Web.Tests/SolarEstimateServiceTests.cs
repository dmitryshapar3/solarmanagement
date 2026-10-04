using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeyeSolar.Web.Tests;

public class SolarEstimateServiceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 18, 11, 20, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Monitor : IOptionsMonitor<SolarEstimateOptions>
    {
        public SolarEstimateOptions CurrentValue { get; } = new() { DeyeSolarPowerIsPvDcConfirmed = true, DeyeConfirmedDeviceSn = "test-device" };
        public SolarEstimateOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SolarEstimateOptions, string?> listener) => null;
    }
    private sealed class Source : ISolarRadiationSource
    {
        public int Calls;
        public bool Fail;
        public SolarRadiationObservation? Result;
        public Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions options, DateTimeOffset now, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("offline");
            return Task.FromResult(Result ?? new(now.AddMinutes(-20), 800, 400, 20, 2, now.AddMinutes(-20), 0.1));
        }
    }
    private sealed class DeyeMonitor : IOptionsMonitor<DeyeCloudOptions>
    {
        public DeyeCloudOptions CurrentValue { get; } = new() { DeviceSn = "test-device" };
        public DeyeCloudOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<DeyeCloudOptions, string?> listener) => null;
    }
    private sealed class Store : ISolarEstimateStore
    {
        public CachedSolarObservation? Cache;
        public SolarActual? Actual;
        public bool FailSave;
        public Task<CachedSolarObservation?> LoadAsync(CancellationToken ct) => Task.FromResult(Cache);
        public Task SaveAsync(CachedSolarObservation observation, CancellationToken ct)
        {
            if (FailSave) throw new InvalidOperationException("Test persistence failure.");
            Cache = observation;
            return Task.CompletedTask;
        }
        public Task<SolarActual?> FindActualAsync(DateTimeOffset timestamp, int toleranceSeconds, DateTimeOffset now, CancellationToken ct) => Task.FromResult(Actual);
    }

    [Theory]
    [InlineData("weather", "The weather API is unavailable or returned incomplete data. The last successful result is retained.")]
    [InlineData("persistence", "The estimate is available but could not be saved for a server restart.")]
    [InlineData("expired", "Fresh weather data for the current minute is unavailable.")]
    [InlineData("missing-actual", "No recent Deye reading has weather data for its measurement time.")]
    [InlineData("unconfirmed", "Confirm that Deye TotalSolarPower is the total PV DC power of this installation.")]
    public async Task PublishedEstimateErrorsAndComparisonDetailsRemainEnglish(string scenario, string expected)
    {
        var clock = new Clock();
        var source = new Source { Result = ModelForecast(clock.Now), Fail = scenario == "weather" };
        var store = new Store { FailSave = scenario == "persistence" };
        var monitor = new Monitor();
        if (scenario == "unconfirmed") monitor.CurrentValue.DeyeSolarPowerIsPvDcConfirmed = false;
        var service = new SolarEstimateService(source, store, monitor, clock,
            NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());

        await service.UpdateAsync(default);
        if (scenario == "expired")
        {
            source.Fail = true;
            clock.Now = clock.Now.AddMinutes(31);
            await service.UpdateAsync(default);
        }

        Assert.Equal(expected, service.Current.Error ?? service.Current.Comparison.Reason);
        foreach (var message in new[] { service.Current.Error, service.Current.Comparison.Reason }.OfType<string>())
            Assert.DoesNotMatch("[\\u0400-\\u04FF]", message);
        Assert.Equal("Waiting for weather data to estimate current power.", SolarEstimateState.Empty.Comparison.Reason);
    }

    [Fact]
    public async Task ScheduledCycleFailurePublishesEnglishError()
    {
        var monitor = new Monitor();
        monitor.CurrentValue.Roof1Kwp = -1;
        var service = new SolarEstimateService(new Source(), new Store(), monitor, new Clock(),
            NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        var published = false;
        service.OnUpdated += () => published = true;
        await service.RunScheduledUpdateAsync(default);
        Assert.True(published);
        Assert.Equal("The estimate could not be refreshed.", service.Current.Error);
        Assert.Equal("A reliable comparison is unavailable.", service.Current.Comparison.Reason);
        Assert.True(service.Current.RefreshFailed);
    }

    [Fact]
    public async Task SharesTenMinuteCacheAndPreservesLastGoodResultOnApiFailure()
    {
        var clock = new Clock(); var source = new Source(); var store = new Store();
        var service = new SolarEstimateService(source, store, new Monitor(), clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        await service.UpdateAsync(default);
        var timestamp = service.Current.Estimate!.Timestamp;
        Assert.NotNull(store.Cache);
        clock.Now = clock.Now.AddMinutes(1);
        await service.UpdateAsync(default);
        Assert.Equal(1, source.Calls);
        source.Fail = true;
        clock.Now = clock.Now.AddMinutes(10);
        await service.UpdateAsync(default);
        Assert.Equal(2, source.Calls);
        Assert.True(service.Current.RefreshFailed);
        Assert.Equal(timestamp, service.Current.Estimate!.Timestamp);
        Assert.Null(service.Current.Comparison.DeviationPercent);
        Assert.Equal(SolarComparisonStatus.InsufficientData, service.Current.Comparison.Status);
    }

    [Fact]
    public async Task RestoresPersistedObservationAfterRestartWithoutFakeZeroOnFailure()
    {
        var clock = new Clock(); var monitor = new Monitor(); var source = new Source { Fail = true }; var store = new Store();
        {
            var empty = new SolarEstimateService(source, store, monitor, clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
            await empty.UpdateAsync(default);
            Assert.Null(empty.Current.Estimate);
        }
        store.Cache = new(SolarEstimateService.ConfigurationKey(monitor.CurrentValue),
            new(clock.Now.AddMinutes(-30), 800, 400, 20, 2, clock.Now.AddMinutes(-30), 0.1), clock.Now.AddMinutes(-10));
        var restored = new SolarEstimateService(source, store, monitor, clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        await restored.UpdateAsync(default);
        Assert.NotNull(restored.Current.Estimate);
        Assert.True(restored.Current.RefreshFailed);
        Assert.True(restored.Current.Estimate!.CentralKw > 0);
    }

    [Fact]
    public async Task RejectsCacheForDifferentInstallationAndDoesNotPersistSecrets()
    {
        var clock = new Clock(); var monitor = new Monitor(); var store = new Store();
        var key = SolarEstimateService.ConfigurationKey(monitor.CurrentValue);
        monitor.CurrentValue.ApiKey = "test-private-key";
        Assert.Equal(key, SolarEstimateService.ConfigurationKey(monitor.CurrentValue));
        store.Cache = new("different-site", new(clock.Now.AddMinutes(-20), 800, 400, 20, 2, clock.Now.AddMinutes(-20), 0.1), clock.Now);
        var service = new SolarEstimateService(new Source { Fail = true }, store, monitor, clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        await service.UpdateAsync(default);
        Assert.Null(service.Current.Estimate);
    }

    [Fact]
    public async Task HistoryAppearingAfterSatelliteFetchEnablesComparisonWithoutNewWeatherCalls()
    {
        var clock = new Clock(); var source = new Source(); var store = new Store();
        var service = new SolarEstimateService(source, store, new Monitor(), clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        await service.UpdateAsync(default);
        Assert.Equal(SolarComparisonStatus.InsufficientData, service.Current.Comparison.Status);
        var estimate = service.Current.Estimate!;
        store.Actual = new(estimate.Timestamp.AddSeconds(60), estimate.CentralKw, SolarPowerBasis.PvDc);
        clock.Now = clock.Now.AddMinutes(1);
        await service.UpdateAsync(default);
        Assert.Equal(1, source.Calls);
        Assert.Equal(SolarComparisonStatus.WithinRange, service.Current.Comparison.Status);
    }

    [Fact]
    public async Task ChangedDeviceRequiresItsOwnDcConfirmation()
    {
        var clock = new Clock(); var monitor = new Monitor(); var device = new DeyeMonitor(); var source = new Source(); var store = new Store();
        device.CurrentValue.DeviceSn = "other-device";
        var service = new SolarEstimateService(source, store, monitor, clock, NullLogger<SolarEstimateService>.Instance, device);
        await service.UpdateAsync(default);
        var estimate = service.Current.Estimate!;
        store.Actual = new(estimate.Timestamp, estimate.CentralKw, SolarPowerBasis.PvDc);
        await service.UpdateAsync(default);
        Assert.Equal(SolarComparisonStatus.InsufficientData, service.Current.Comparison.Status);
        Assert.Null(service.Current.Comparison.DeviationPercent);
    }

    [Fact]
    public async Task FutureOrRegressingResponseKeepsLastGoodObservation()
    {
        var clock = new Clock(); var source = new Source(); var store = new Store();
        var service = new SolarEstimateService(source, store, new Monitor(), clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        await service.UpdateAsync(default);
        var previous = service.Current.Estimate!;
        source.Result = previous.Observation with { Timestamp = clock.Now.AddHours(1) };
        clock.Now = clock.Now.AddMinutes(10);
        await service.UpdateAsync(default);
        Assert.True(service.Current.RefreshFailed);
        Assert.Equal(previous.Timestamp, service.Current.Estimate!.Timestamp);
    }

    private static SolarRadiationObservation ModelForecast(DateTimeOffset now) => new(now, 800, 400, 20, 2, now, 0)
    {
        Kind = SolarRadiationKind.WeatherModel, RetrievedAt = now,
        Forecast = Enumerable.Range(-2, 7).Select(i => new SolarWeatherSample(now.AddMinutes(i * 15),
            600 + 100 * i, 300 + 50 * i, 20, 2, 25)).ToArray()
    };

    [Fact]
    public async Task CorrectedGeometryDiscardsLegacyCacheAndFreshlyCalculatesBothRoofs()
    {
        var clock = new Clock(); var monitor = new Monitor();
        monitor.CurrentValue.Roof1Kwp = 4.05;
        monitor.CurrentValue.Roof2Kwp = 4.05;
        monitor.CurrentValue.Roof1Tilt = 23;
        monitor.CurrentValue.Roof2Tilt = 23;
        var oldKey = SolarEstimateService.ConfigurationKey(monitor.CurrentValue);
        var store = new Store { Cache = new(oldKey, ModelForecast(clock.Now), clock.Now) };
        monitor.CurrentValue.Roof1Kwp = 4.32;
        monitor.CurrentValue.Roof2Kwp = 3.78;
        monitor.CurrentValue.Roof1Tilt = 25;
        monitor.CurrentValue.Roof2Tilt = 25;
        var source = new Source { Fail = true, Result = ModelForecast(clock.Now) };
        var service = new SolarEstimateService(source, store, monitor, clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());

        await service.UpdateAsync(default);
        Assert.Null(service.Current.Estimate);
        Assert.True(service.Current.RefreshFailed);
        Assert.Equal(oldKey, store.Cache.ConfigurationKey);

        clock.Now = clock.Now.AddMinutes(10);
        source.Fail = false;
        await service.UpdateAsync(default);
        Assert.False(service.Current.RefreshFailed);
        Assert.NotEqual(oldKey, store.Cache.ConfigurationKey);
        Assert.Equal(4.32, service.Current.Estimate!.Roofs[0].CapacityKwp);
        Assert.Equal(3.78, service.Current.Estimate.Roofs[1].CapacityKwp);
        Assert.All(service.Current.Estimate.Roofs, roof => Assert.Equal(25, roof.Tilt));
        Assert.Equal(clock.Now, service.Current.Estimate.Timestamp);
    }

    [Fact]
    public async Task ModelEvaluatesCurrentMinuteAndDeyeTimeSeparatelyWithoutAnotherWeatherRequest()
    {
        var clock = new Clock(); var source = new Source { Result = ModelForecast(clock.Now) };
        var store = new Store { Actual = new(clock.Now.AddMinutes(-5), 3, SolarPowerBasis.PvDc) };
        var service = new SolarEstimateService(source, store, new Monitor(), clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        await service.UpdateAsync(default);
        var first = service.Current.Estimate!;
        Assert.Equal(clock.Now, first.Timestamp);
        Assert.Equal(store.Actual.Timestamp, service.Current.ComparisonEstimate!.Timestamp);
        Assert.NotEqual(first.CentralKw, service.Current.ComparisonEstimate.CentralKw);
        Assert.NotNull(service.Current.Comparison.DeviationPercent);
        clock.Now = clock.Now.AddMinutes(1);
        await service.UpdateAsync(default);
        Assert.Equal(1, source.Calls);
        Assert.Equal(clock.Now, service.Current.Estimate!.Timestamp);
        Assert.NotEqual(first.CentralKw, service.Current.Estimate.CentralKw);
        Assert.Equal(first.Observation.RetrievedAt, service.Current.Estimate.Observation.RetrievedAt);
    }

    [Fact]
    public async Task StopsPresentingExpiredForecastAsNowAndRejectsOldDeye()
    {
        var clock = new Clock(); var source = new Source { Result = ModelForecast(clock.Now) };
        var store = new Store { Actual = new(clock.Now.AddMinutes(-11), 3, SolarPowerBasis.PvDc) };
        var service = new SolarEstimateService(source, store, new Monitor(), clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        await service.UpdateAsync(default);
        Assert.Null(service.Current.ComparisonEstimate);
        Assert.Equal(SolarComparisonStatus.InsufficientData, service.Current.Comparison.Status);
        source.Fail = true;
        clock.Now = clock.Now.AddMinutes(31);
        await service.UpdateAsync(default);
        Assert.Null(service.Current.Estimate);
        Assert.True(service.Current.RefreshFailed);
    }

    [Fact]
    public async Task DoesNotFreezeLastModelValueWhenForecastWindowEnds()
    {
        var clock = new Clock(); var source = new Source { Result = ModelForecast(clock.Now) with
            { Forecast = [new(clock.Now.AddMinutes(-15), 500, 300, 20, 2, 30), new(clock.Now, 600, 400, 20, 2, 30)] } };
        var service = new SolarEstimateService(source, new Store(), new Monitor(), clock, NullLogger<SolarEstimateService>.Instance, new DeyeMonitor());
        await service.UpdateAsync(default);
        Assert.NotNull(service.Current.Estimate);
        clock.Now = clock.Now.AddMinutes(1);
        await service.UpdateAsync(default);
        Assert.Null(service.Current.Estimate);
        Assert.True(service.Current.RefreshFailed);
    }
}
