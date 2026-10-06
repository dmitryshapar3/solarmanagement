using System.Globalization;
using System.Text.Json;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Infrastructure.Solar;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolarManagement.Inverters.Contracts;
using Basis=DeyeSolar.Domain.Models.SolarPowerBasis;

namespace DeyeSolar.Web.Tests;

public sealed class SolarProductionServiceTests
{
    private static readonly DateTimeOffset Now=new(2026,10,5,12,30,0,TimeSpan.Zero);
    private sealed class Clock:TimeProvider {public DateTimeOffset Current=Now;public override DateTimeOffset GetUtcNow()=>Current;}
    private sealed class Monitor<T>(T value):IOptionsMonitor<T>
    {public T CurrentValue {get;set;}=value;public T Get(string? name)=>CurrentValue;public IDisposable? OnChange(Action<T,string?> listener)=>null;}
    private sealed class Forecast:ISolarDayForecastSource
    {
        public int Calls;public bool Fail;public Action? OnRead;public DateTimeOffset? RetrievedAt;
        public Task<SolarDayForecast> ReadAsync(SolarEstimateOptions options,DateTimeOffset start,DateTimeOffset end,DateOnly date,CancellationToken ct)
        {
            Calls++;OnRead?.Invoke();if(Fail)throw new HttpRequestException();
            var samples=Enumerable.Range(0,(int)(end-start).TotalHours).Select(i=>new SolarWeatherSample(start.AddHours(i),500,500,20,1,0)).ToArray();
            return Task.FromResult(new SolarDayForecast(samples,RetrievedAt??Now,Now.AddHours(-7),Now.AddHours(5),Now.AddHours(17)));
        }
    }
    private sealed class Store:ISolarHistoryStore
    {
        public int Calls;public Action? OnRead;public DateTimeOffset? ReadThrough;public IReadOnlyList<SolarActual> Samples=Enumerable.Range(0,19).Select(i=>new SolarActual(Now.AddMinutes(-90+i*5),2,Basis.PvDc)).ToArray();
        public Task<IReadOnlyList<SolarActual>> ReadAsync(string device,DateTimeOffset start,DateTimeOffset end,CancellationToken ct)
        {Calls++;ReadThrough=end;OnRead?.Invoke();return Task.FromResult(Samples);}
    }
    private sealed class Fixture
    {
        public Forecast Weather=new();public Store Actual=new();
        public Monitor<SolarEstimateOptions> Config=new(new(){TimeZoneId="UTC",DeyeSolarPowerIsPvDcConfirmed=true,DeyeConfirmedDeviceSn="primary"});
        public Monitor<InverterConnectionOptions> Source=new(new(){DeviceKey="primary"});
        public SolarProductionService Service(TimeProvider? clock=null)=>new(Weather,Actual,Config,Source,clock??new Clock(),NullLogger<SolarProductionService>.Instance);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ColdTodayAndHistoricalForecastsLoadWhenTheRealWeatherClientCapturesALaterClock(int daysAgo)
    {
        var clock=new AdvancingClock();
        var transport=new SyntheticWeather();
        var source=new OpenMeteoSolarHistoryClient(transport,clock);
        var config=new Monitor<SolarEstimateOptions>(new(){TimeZoneId="Europe/Warsaw",DeyeSolarPowerIsPvDcConfirmed=true,DeyeConfirmedDeviceSn="primary"});
        var actual=new Store(){Samples=[]};
        var service=new SolarProductionService(source,actual,config,new Monitor<InverterConnectionOptions>(new(){DeviceKey="primary"}),clock,NullLogger<SolarProductionService>.Instance);
        var selected=new DateOnly(2026,10,5).AddDays(-daysAgo);

        var result=await service.ReadAsync(SolarHistoryPeriod.Today,selected);

        Assert.Equal(selected,result.Date);
        Assert.Equal(new DateOnly(2026,10,5),result.Today);
        Assert.Equal(24,result.Hours.Count);
        Assert.All(result.Hours,hour=>Assert.NotNull(hour.ExpectedKw));
        Assert.NotNull(result.ExpectedEnergyKwh);
        Assert.Null(result.WeatherError);
        Assert.True(result.ForecastRetrievedAt>Now);
        Assert.Equal(3,transport.Calls);
        var repeated=await service.ReadAsync(SolarHistoryPeriod.Today,selected);
        Assert.Equal(result.ForecastRetrievedAt,repeated.ForecastRetrievedAt);
        Assert.Equal(3,transport.Calls);
    }

    [Fact]
    public async Task LaterEvaluationDoesNotMoveTheEntryMeasurementCutoffOrCompletedHourBoundary()
    {
        var clock=new Clock();
        var fixture=new Fixture();
        fixture.Weather.OnRead=()=>clock.Current=Now.AddMinutes(15);
        fixture.Weather.RetrievedAt=Now.AddMinutes(15);
        fixture.Actual.OnRead=()=>clock.Current=Now.AddHours(2);
        fixture.Actual.Samples=fixture.Actual.Samples.Concat([
            new SolarActual(Now.AddMinutes(5),100,Basis.PvDc),new SolarActual(Now.AddMinutes(10),100,Basis.PvDc)]).ToArray();

        var result=await fixture.Service(clock).ReadAsync(SolarHistoryPeriod.Today);

        Assert.Equal(Now.AddTicks(1),fixture.Actual.ReadThrough);
        Assert.Equal(3,result.ObservedEnergyKwh!.Value,10);
        Assert.Equal(2,result.CompletedEnergyKwh!.Value,10);
        Assert.Equal(Now.Date,new DateTime(result.Today.Year,result.Today.Month,result.Today.Day));
        Assert.Equal(new DateTimeOffset(Now.Year,Now.Month,Now.Day,Now.Hour,0,0,TimeSpan.Zero),result.CurrentHour!.Timestamp);
        Assert.Equal(1,result.CurrentHour.ObservedEnergyKwh!.Value,10);
        Assert.Null(result.Hours.Single(hour=>hour.Timestamp.Hour==Now.Hour).ActualKw);
        Assert.All(result.Hours.Where(hour=>hour.Timestamp>Now),hour=>Assert.Null(hour.ObservedEnergyKwh));
    }

    [Fact]
    public async Task AModelRetrievedAfterTheEvaluationClockStillFailsTheUnchangedFutureGuard()
    {
        var fixture=new Fixture();
        fixture.Weather.RetrievedAt=Now.AddMilliseconds(1);
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>fixture.Service(new Clock()).ReadAsync(SolarHistoryPeriod.Today));
        Assert.Equal("A forecast requires a retrieved weather model.",error.Message);
    }

    private sealed class AdvancingClock:TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow()=>Now.AddMilliseconds(calls++);
    }
    private sealed class SyntheticWeather:IOpenMeteoJsonReader
    {
        public int Calls;
        public Task<JsonDocument> ReadAsync(Uri uri,CancellationToken ct)
        {
            Calls++;
            if(uri.Query.Contains("daily="))return Task.FromResult(JsonDocument.Parse("{}"));
            var query=uri.Query.TrimStart('?').Split('&').Select(pair=>pair.Split('=',2)).ToDictionary(pair=>pair[0],pair=>Uri.UnescapeDataString(pair[1]));
            var start=new DateTimeOffset(DateOnly.ParseExact(query["start_date"],"yyyy-MM-dd",CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
            var end=new DateTimeOffset(DateOnly.ParseExact(query["end_date"],"yyyy-MM-dd",CultureInfo.InvariantCulture).AddDays(1).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
            var count=(int)(end-start).TotalHours;
            var json=JsonSerializer.Serialize(new {hourly_units=new {time="unixtime",global_tilted_irradiance="W/m²",temperature_2m="°C",wind_speed_10m="m/s",cloud_cover="%"},hourly=new {
                time=Enumerable.Range(0,count).Select(i=>start.AddHours(i).ToUnixTimeSeconds()),global_tilted_irradiance=Enumerable.Repeat(500,count),
                temperature_2m=Enumerable.Repeat(20,count),wind_speed_10m=Enumerable.Repeat(1,count),cloud_cover=Enumerable.Repeat(0,count)}});
            return Task.FromResult(JsonDocument.Parse(json));
        }
    }

    [Fact] public async Task FullDayForecastAndCompletedMeansKeepCurrentEnergySeparate()
    {
        var f=new Fixture();var service=f.Service();var data=await service.ReadAsync(SolarHistoryPeriod.Today);
        Assert.Equal(24,data.Hours.Count);Assert.All(data.Hours,h=>Assert.NotNull(h.ExpectedKw));
        Assert.Equal(2,Assert.Single(data.Hours.Where(h=>h.Timestamp.Hour==11)).ActualKw);
        Assert.Null(Assert.Single(data.Hours.Where(h=>h.Timestamp.Hour==12)).ActualKw);
        Assert.All(data.Hours.Where(h=>h.Timestamp>Now),h=>{Assert.Null(h.ActualKw);Assert.Null(h.ObservedEnergyKwh);});
        Assert.Equal(3,data.ObservedEnergyKwh!.Value,10);Assert.Equal(2,data.CompletedEnergyKwh!.Value,10);Assert.Equal(1,data.CurrentHour!.ObservedEnergyKwh!.Value,10);Assert.Null(data.CurrentHour.ActualKw);
        Assert.Equal(data.ObservedEnergyKwh,Assert.Single(data.Days).ObservedEnergyKwh);Assert.Equal(5400,data.CoveredSeconds);Assert.True(data.Partial);
        Assert.Equal(2,data.BestHour!.ActualKw);Assert.NotNull(data.ExpectedEnergyKwh);Assert.NotNull(data.NextSunrise);
        await service.ReadAsync(SolarHistoryPeriod.Today);Assert.Equal(1,f.Weather.Calls);
    }
    [Fact] public async Task UnknownPowerIsNeverReadOrPresentedAsZeroAndWeatherFailureStillKeepsRealEnergy()
    {
        var f=new Fixture();f.Config.CurrentValue.DeyeSolarPowerIsPvDcConfirmed=false;
        var unconfirmed=await f.Service().ReadAsync(SolarHistoryPeriod.Today);Assert.Equal(0,f.Actual.Calls);Assert.Null(unconfirmed.ObservedEnergyKwh);Assert.NotNull(unconfirmed.ActualError);
        f.Config.CurrentValue.DeyeSolarPowerIsPvDcConfirmed=true;f.Weather.Fail=true;
        var outage=await f.Service().ReadAsync(SolarHistoryPeriod.Today);Assert.Equal(3,outage.ObservedEnergyKwh!.Value,10);Assert.Null(outage.ExpectedEnergyKwh);Assert.Null(outage.Sunrise);Assert.NotNull(outage.WeatherError);
    }
    [Fact] public async Task SourceChangesDuringIoRejectTheMixedSnapshot()
    {
        var f=new Fixture();f.Actual.OnRead=()=>f.Source.CurrentValue=new(){DeviceKey="replacement"};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service().ReadAsync(SolarHistoryPeriod.Today));
    }
    [Theory] [InlineData(SolarHistoryPeriod.Week,7)] [InlineData(SolarHistoryPeriod.Month,30)]
    public async Task DailyRowsCoverTheSelectedCalendarWindow(SolarHistoryPeriod period,int days)
    {
        var f=new Fixture();var result=await f.Service().ReadAsync(period);
        Assert.Equal(days,result.Days.Count);Assert.Equal(days*24,result.Hours.Count);Assert.Equal(result.ObservedEnergyKwh,result.Days.Where(d=>d.ObservedEnergyKwh.HasValue).Sum(d=>d.ObservedEnergyKwh));
        Assert.True(result.Days.Take(days-1).All(d=>d.Partial&&d.ObservedEnergyKwh is null));
    }
}
