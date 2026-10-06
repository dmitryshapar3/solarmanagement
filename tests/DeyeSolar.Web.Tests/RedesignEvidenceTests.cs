using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Operations;
using DeyeSolar.Web.Redesign;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DeyeSolar.Web.Tests;

public sealed class RedesignEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static SolarActual Sample(double minutes, double kw) => new(Start.AddMinutes(minutes), kw, SolarPowerBasis.PvDc);
    [Fact] public void RealZeroIsMeasuredWhileGapsAndSingleSamplesRemainUnknown()
    {
        var measured = SolarEnergyIntegration.Integrate([Sample(0, 0), Sample(5, 0), Sample(10, 0)], Start, Start.AddMinutes(10));
        Assert.Equal(0d, measured.EnergyKwh); Assert.Equal(600, measured.CoveredSeconds); Assert.False(measured.Partial);
        var gap = SolarEnergyIntegration.Integrate([Sample(0, 6), Sample(11, 6)], Start, Start.AddMinutes(11));
        Assert.Null(gap.EnergyKwh); Assert.Equal(0, gap.CoveredSeconds); Assert.True(gap.Partial);
        Assert.Null(SolarEnergyIntegration.Integrate([Sample(0, 6)], Start, Start.AddMinutes(10)).EnergyKwh);
    }
    [Fact] public void IntegrationClipsBoundariesAndUsesOneMeasurementPerTimestamp()
    {
        var result = SolarEnergyIntegration.Integrate([Sample(-5, 0), Sample(5, 6), Sample(5, 12)], Start, Start.AddMinutes(5));
        Assert.Equal(.75, result.EnergyKwh!.Value, 10); Assert.Equal(300, result.CoveredSeconds);
        var invalid = Sample(2, 100) with { Basis = SolarPowerBasis.Ac };
        Assert.Equal(result, SolarEnergyIntegration.Integrate([Sample(-5, 0), invalid, Sample(5, 12)], Start, Start.AddMinutes(5)));
    }
    [Theory] [InlineData(2026, 3, 29, 23)] [InlineData(2026, 10, 25, 25)]
    public void EnergyCoverageUsesActualSecondsAcrossDst(int year, int month, int day, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var date = new DateTime(year, month, day);
        var from = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date, zone), TimeSpan.Zero);
        var to = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1), zone), TimeSpan.Zero);
        var samples = Enumerable.Range(0, hours * 12 + 1).Select(i => new SolarActual(from.AddMinutes(i * 5), 1, SolarPowerBasis.PvDc)).ToArray();
        var result = SolarEnergyIntegration.Integrate(samples, from, to);
        Assert.Equal(hours, result.EnergyKwh!.Value, 8); Assert.Equal(hours * 3600, result.ExpectedSeconds); Assert.False(result.Partial);
    }
    [Fact] public void TimelineLimitsPhysicalEvidenceAndNeverUsesAcknowledgedCommandsAsObservedState()
    {
        var rows = new[]
        {
            new ActivityEvent { Id=1, Kind="device.observed", OccurredAt=Start.UtcDateTime, State=true },
            new ActivityEvent { Id=2, Kind="device.observed", OccurredAt=Start.AddMinutes(20).UtcDateTime, State=false },
            new ActivityEvent { Id=3, Kind="device.observed", OccurredAt=Start.AddMinutes(30).UtcDateTime, State=true }
        };
        var result = RedesignQueries.BuildDeviceHistory(rows, Start, Start.AddMinutes(30));
        Assert.Equal(600, result.OnSeconds); Assert.Equal(1200, result.KnownSeconds); Assert.True(result.Partial);
        Assert.Equal(1800, result.Intervals.Sum(i => (i.To - i.From).TotalSeconds));
        Assert.Single(result.Intervals.Where(i => i.IsOn is null));
    }
    [Fact] public void AReplacementGenerationAtTheSameTimestampWinsWithoutOverlappingIntervals()
    {
        var rows = new[]
        {
            new ActivityEvent {Id=1,OccurredAt=Start.UtcDateTime,Generation=1,State=true},
            new ActivityEvent {Id=2,OccurredAt=Start.UtcDateTime,Generation=2,State=null},
            new ActivityEvent {Id=3,OccurredAt=Start.AddMinutes(5).UtcDateTime,Generation=2,State=false}
        };
        var result=RedesignQueries.BuildDeviceHistory(rows,Start,Start.AddMinutes(10));
        Assert.Equal(0,result.OnSeconds); Assert.Equal(300,result.KnownSeconds); Assert.True(result.Partial);
        Assert.Equal(600,result.Intervals.Sum(i=>(i.To-i.From).TotalSeconds));
    }
    [Theory]
    [InlineData("/Login", "/signin")] [InlineData("/rules/edit/42", "/automations/42")]
    [InlineData("/sales-details", "/energy/export")] [InlineData("/billing", "/settings/account")]
    [InlineData("/history", "/activity/readings")] [InlineData("/rules/edit", "/automations/new")]
    public void LegacyBookmarkMappingPreservesDestinations(string old, string target) => Assert.Equal(target, LegacyUiRedirects.Destination(old));
    [Fact] public async Task RedirectsPreserveQueriesAndNeverRedirectApiOrPost()
    {
        var calls=0; var middleware=new LegacyUiRedirects(_=>{calls++;return Task.CompletedTask;},new EnvironmentStub());
        var context=new DefaultHttpContext(); context.Request.Method="GET";context.Request.Path="/sales";context.Request.QueryString=new("?period=custom&from=2026-10-01");
        await middleware.InvokeAsync(context);Assert.Equal(301,context.Response.StatusCode);Assert.Equal("/energy/export?period=custom&from=2026-10-01",context.Response.Headers.Location);
        context.Request.Method="POST";await middleware.InvokeAsync(context); Assert.Equal(1,calls);
        context.Request.Method="GET";context.Request.Path="/api/rules";await middleware.InvokeAsync(context);Assert.Equal(2,calls);
        context.Request.Path="/_design"; await middleware.InvokeAsync(context);Assert.Equal(404,context.Response.StatusCode);Assert.Equal(2,calls);
    }
    private sealed class EnvironmentStub:IHostEnvironment
    {
        public string EnvironmentName {get;set;}="Production"; public string ApplicationName {get;set;}="Tests";
        public string ContentRootPath {get;set;}="";public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider {get;set;}=new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
