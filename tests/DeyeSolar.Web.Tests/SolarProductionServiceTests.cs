using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
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
    private sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Monitor<T>(T value):IOptionsMonitor<T>
    {public T CurrentValue {get;set;}=value;public T Get(string? name)=>CurrentValue;public IDisposable? OnChange(Action<T,string?> listener)=>null;}
    private sealed class Forecast:ISolarDayForecastSource
    {
        public int Calls;public bool Fail;
        public Task<SolarDayForecast> ReadAsync(SolarEstimateOptions options,DateTimeOffset start,DateTimeOffset end,DateOnly date,CancellationToken ct)
        {
            Calls++;if(Fail)throw new HttpRequestException();
            var samples=Enumerable.Range(0,(int)(end-start).TotalHours).Select(i=>new SolarWeatherSample(start.AddHours(i),500,500,20,1,0)).ToArray();
            return Task.FromResult(new SolarDayForecast(samples,Now,Now.AddHours(-7),Now.AddHours(5),Now.AddHours(17)));
        }
    }
    private sealed class Store:ISolarHistoryStore
    {
        public int Calls;public Action? OnRead;public IReadOnlyList<SolarActual> Samples=Enumerable.Range(0,19).Select(i=>new SolarActual(Now.AddMinutes(-90+i*5),2,Basis.PvDc)).ToArray();
        public Task<IReadOnlyList<SolarActual>> ReadAsync(string device,DateTimeOffset start,DateTimeOffset end,CancellationToken ct)
        {Calls++;OnRead?.Invoke();return Task.FromResult(Samples);}
    }
    private sealed class Fixture
    {
        public Forecast Weather=new();public Store Actual=new();
        public Monitor<SolarEstimateOptions> Config=new(new(){TimeZoneId="UTC",DeyeSolarPowerIsPvDcConfirmed=true,DeyeConfirmedDeviceSn="primary"});
        public Monitor<InverterConnectionOptions> Source=new(new(){DeviceKey="primary"});
        public SolarProductionService Service()=>new(Weather,Actual,Config,Source,new Clock(),NullLogger<SolarProductionService>.Instance);
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
