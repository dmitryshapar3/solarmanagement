using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using DeyeSolar.Web.Pages;
using DeyeSolar.Web.Components.Charts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace DeyeSolar.Web.Tests;

// Regression coverage follows the shipped Export page/plot, rather than retaining the replaced UI.
public class SalesStatisticsTests
{
    private static readonly DateOnly Today = new(2026,9,30);
    private static readonly DateTimeOffset Start = new(2026,9,29,22,0,0,TimeSpan.Zero);
    [Theory]
    [InlineData("normal")][InlineData("zero")][InlineData("partial")][InlineData("error")][InlineData("empty")][InlineData("before-contract")][InlineData("month")][InlineData("year")][InlineData("custom")]
    public async Task ExportPageUsesEnglishAndKeepsFinancialProvenance(string scenario)
    {
        var data=Scenario(scenario);var html=await RenderAsync(data);
        Assert.DoesNotMatch("[\\u0400-\\u04ff]",html);Assert.Contains("Exported to grid",html);Assert.Contains("Energy value",html);Assert.Contains("Estimated deposit",html);
        Assert.Contains("OSD billing meter",html);Assert.Contains("not a payout or your current balance",html);Assert.Contains("Completed hours only",html);
        Assert.Contains("aria-label=\"Chart period\"",html);Assert.Contains("Download CSV",html);Assert.DoesNotContain("Refresh sales",html);
        foreach(var period in new[]{"Day","7 days","30 days","Month","Custom"})Assert.Contains(">"+period+"</button>",html);
        Assert.DoesNotContain(">Year</button>",html);
        if(scenario=="normal"){Assert.Contains("1.20",html);Assert.Contains("5.00",html);}
        if(scenario=="error")Assert.Contains("Deye history is unavailable.",html);
        if(scenario=="partial"){Assert.Contains("Partial data",html);Assert.Contains("price has not been published",html);Assert.Contains("Some completed hours",html);}
    }
    [Fact] public async Task TotalsKeepExportEnergyValueAndEstimatedCreditDistinct()
    {
        var html=await RenderAsync(Result());Assert.Matches("data-testid=\"sales-export\"[^>]*>5[.]00",html);Assert.Matches("data-testid=\"sales-value\"[^>]*>1[.]20",html);Assert.Matches("data-testid=\"sales-deposit\"[^>]*>1[.]48",html);
        Assert.Contains("Estimated credit · × 1.23",html);Assert.Contains("Contract starts",html);Assert.Contains("Europe/Warsaw",html);
    }
    [Fact]
    public async Task ExportSeparatesCreditedNetEnergyAndMeasuredVersusPricedCoverage()
    {
        var data=Result() with{CreditedExportKwh=3m,ExpectedHours=4,ObservedHours=3,ValuedHours=2};
        var html=await RenderAsync(data);
        Assert.Matches("data-testid=\"sales-credited\"[^>]*>Credited after hourly netting: 3[.]00 kWh",html);
        Assert.Matches("data-testid=\"sales-measured\"[^>]*>Measured hours: 3 / 4",html);
        Assert.Contains("Priced hours: 2 / 4",html);
        Assert.Contains("netted for each hour",html);
        Assert.Contains("available RCE prices",html);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CurrentHourIsSeparateFromCompletedTotalsAndNeverProjected(bool priceKnown)
    {
        var data=Result() with{Buckets=[Result().Buckets[0],new(Start.AddHours(1),Start.AddHours(2),null,null,null,null,0,0,0)],CurrentHour=new(Start.AddHours(1),Start.AddHours(1).AddMinutes(15),.25m,.20m,priceKnown?.08m:null,priceKnown?.0984m:null,900)};
        var html=await RenderAsync(data);Assert.Contains("Current hour · in progress",html);Assert.Contains("Excluded from totals",html);Assert.Contains("0.25",html);Assert.Contains("5.00",html);Assert.DoesNotContain("5.25",html);Assert.Single(Regex.Matches(html,"data-testid=\"sales-progress-bar\""));Assert.Single(Regex.Matches(html,"data-testid=\"sales-bar\""));Assert.DoesNotContain("6.00",html);
    }
    [Theory][InlineData("zero",true)][InlineData("missing",false)]
    public async Task MeasuredZeroGetsVisibleBaselineAndMissingEnergyRemainsAGap(string scenario,bool visible)
    {
        var data=Result() with{ExportKwh=visible?0:null,EnergyValuePln=visible?0:null,EstimatedDepositPln=visible?0:null,Buckets=[new(Start,Start.AddHours(1),visible?0:null,visible?0:null,visible?0:null,visible?0:null,1,visible?1:0,visible?1:0)]};
        var html=await RenderAsync(data);Assert.Equal(visible,html.Contains("data-testid=\"sales-bar\""));if(visible){Assert.Contains("height=\"1\"",html);Assert.Contains("0.00",html);}else Assert.Contains("Partial readings",html);Assert.Contains("A dash means unavailable",html);
    }
    [Fact] public async Task FutureBucketsStayGapsAndIncompleteCoverageStaysExplicit()
    {
        var data=Result() with{ExpectedHours=2,Buckets=[Result().Buckets[0],new(Start.AddHours(1),Start.AddHours(2),null,null,null,null,1,0,0),new(Start.AddDays(1),Start.AddDays(1).AddHours(1),null,null,null,null,0,0,0)]};
        var html=await RenderAsync(data);Assert.Single(Regex.Matches(html,"data-testid=\"sales-bar\""));Assert.Contains("Partial readings",html);Assert.Contains("Upcoming",html);Assert.Contains("Partial data",html);Assert.Contains("5.00",html);
    }
    [Theory][InlineData(ExportSalesPeriod.Month)][InlineData(ExportSalesPeriod.Year)][InlineData(ExportSalesPeriod.Custom)]
    public async Task CalendarBucketsKeepProvisionalIncrementDistinct(ExportSalesPeriod period)
    {
        var data=Result(new(period,Today,period==ExportSalesPeriod.Custom?Today:null,period==ExportSalesPeriod.Custom?Today:null)) with{CurrentHour=new(Start,Start.AddMinutes(15),.25m,.20m,.1m,.123m,900)};
        var html=await RenderAsync(data);Assert.Single(Regex.Matches(html,"data-testid=\"sales-bar\""));Assert.Single(Regex.Matches(html,"data-testid=\"sales-progress-bar\""));Assert.Contains("In progress",html);Assert.DoesNotContain("5.25",html);
    }
    [Theory][InlineData(ExportSalesPeriod.Day)][InlineData(ExportSalesPeriod.Month)][InlineData(ExportSalesPeriod.Year)]
    public async Task PeriodBeforeContractDoesNotInventZeroSales(ExportSalesPeriod period)
    {
        var data=Result(new(period,Today.AddYears(-1))) with{ExpectedHours=0,ObservedHours=0,ValuedHours=0,ExportKwh=null,EnergyValuePln=null,EstimatedDepositPln=null,Buckets=[new(Start.AddYears(-1),Start.AddYears(-1).AddHours(1),null,null,null,null,0,0,0)]};
        var html=await RenderAsync(data);Assert.Contains("Before contract",html);Assert.DoesNotContain("data-testid=\"sales-bar\"",html);Assert.DoesNotContain(">0.00<",html);
    }
    [Fact] public async Task RepeatedAutumnHoursRetainBothOffsetsAndIndependentBars()
    {
        var at=new DateTimeOffset(2026,10,25,0,0,0,TimeSpan.Zero);var data=Result() with{Buckets=[new(at,at.AddHours(1),1,1,1,1.23m,1,1,1),new(at.AddHours(1),at.AddHours(2),2,2,2,2.46m,1,1,1)]};
        var html=await RenderAsync(data);Assert.Contains("02:00 +02:00",html);Assert.Contains("02:00 +01:00",html);Assert.Equal(2,Regex.Matches(html,"data-testid=\"sales-bar\"").Count);
    }
    [Theory][InlineData("en-GB")][InlineData("pl-PL")]
    public async Task PlotCoordinatesAreInvariantAndAmountsUseTheChosenCulture(string culture)
    {
        var before=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);var html=await RenderAsync(Result());var tag=Regex.Match(html,"<rect[^>]*data-testid=\"sales-bar\"[^>]*>").Value;Assert.Matches("x=\"[0-9.]+\"",tag);Assert.DoesNotContain(",",tag);Assert.Contains(culture=="pl-PL"?"1,20":"1.20",html);}finally{CultureInfo.CurrentCulture=before;}
    }
    [Fact] public async Task MarketPricesRemainSignedAndMissingPriceIsNotZero()
    {
        var data=Result() with{EnergyValuePln=-.6m,EstimatedDepositPln=-.738m,Hours=[new(Start,5,2,3,-.6m,3600,-.12m){MarketAveragePricePlnPerKwh=-.12m}],Buckets=[Result().Buckets[0] with{EnergyValuePln=-.6m,EstimatedDepositPln=-.738m}]};
        var html=await RenderAsync(data);Assert.Contains("-0.1200",html);Assert.Contains("-0.60",html);Assert.Contains("-0.74",html);Assert.Contains("retain their signs",html);
    }
    [Theory][InlineData(0)][InlineData(-.6)][InlineData(1.2)]
    public async Task PlotMetricSwitchKeepsSignedAmountOrMeasuredZeroWithoutRefetch(double money)
    {
        var history=new SalesService{ResultFactory=r=>Result(r) with{Buckets=[Result().Buckets[0] with{EnergyValuePln=(decimal)money}]}};await using var services=Services(history);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();await renderer.ClickAsync(root,"Value");Assert.Contains("PLN",renderer.Text(root));Assert.Single(history.Calls);Assert.Single(renderer.Attributes(root,"data-testid").Where(v=>v=="sales-bar"));if(money==0)Assert.Contains("1",renderer.Attributes(root,"height"));await renderer.ClickAsync(root,"Energy");Assert.Single(history.Calls);Assert.Contains("5.00",renderer.Text(root));});
    }
    [Fact] public async Task MissingMoneyDoesNotHideMeasuredEnergyOrCreateAValueBar()
    {
        var history=new SalesService{MissingPrices=true};await using var services=Services(history);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();Assert.Contains("5.00",renderer.Text(root));await renderer.ClickAsync(root,"Value");Assert.DoesNotContain("sales-bar",renderer.Attributes(root,"data-testid"));Assert.Single(history.Calls);await renderer.ClickAsync(root,"Energy");Assert.Contains("sales-bar",renderer.Attributes(root,"data-testid"));});
    }
    [Fact] public async Task KeyboardInspectorRetainsSelectedHourWhileChangingMetric()
    {
        var history=new SalesService{ResultFactory=MultipleHours};await using var services=Services(history);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();await renderer.KeyAsync(root,"Home");Assert.Contains(Start.ToString("O"),renderer.Attributes(root,"datetime"));await renderer.KeyAsync(root,"ArrowRight");Assert.Contains(Start.AddHours(1).ToString("O"),renderer.Attributes(root,"datetime"));await renderer.ClickAsync(root,"Value");Assert.Contains(Start.AddHours(1).ToString("O"),renderer.Attributes(root,"datetime"));Assert.Single(history.Calls);});
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task AutomaticRefreshKeepsGraphAndPinnedCursorUntilReplacement(bool pin)
    {
        var clock=new ManualClock(Start.AddHours(3));var history=new SalesService{ResultFactory=MultipleHours};await using var services=Services(history,clock);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();if(pin)await renderer.KeyAsync(root,"Home");history.HoldNext=true;clock.Advance(TimeSpan.FromMinutes(5));await renderer.WaitForAsync(()=>history.Calls.Count==2);Assert.Contains("Refreshing…",renderer.Text(root));Assert.Equal(3,renderer.Attributes(root,"data-testid").Count(v=>v=="sales-bar"));history.Complete(Today);await renderer.WaitForAsync(()=>!renderer.Text(root).Contains("Refreshing…"));Assert.Contains(Start.AddHours(pin?0:2).ToString("O"),renderer.Attributes(root,"datetime"));});
    }
    [Fact] public async Task RefreshFailurePreservesTimestampAndExplicitlyLabelsOldValues()
    {
        var clock=new ManualClock(Start.AddHours(3));var history=new SalesService{ResultFactory=r=>MultipleHours(r) with{UpdatedAt=Start.AddHours(2)}};await using var services=Services(history,clock);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();history.FailNext=true;clock.Advance(TimeSpan.FromMinutes(5));await renderer.WaitForAsync(()=>renderer.Text(root).Contains("Refresh failed"));Assert.Contains("values may be out of date",renderer.Text(root));Assert.Contains("6.00",renderer.Text(root));Assert.Contains(Start.AddHours(2).ToString("O"),renderer.Attributes(root,"datetime"));clock.Advance(TimeSpan.FromMinutes(5));await renderer.WaitForAsync(()=>history.Calls.Count==3&&!renderer.Text(root).Contains("Refresh failed"));});
    }
    [Fact] public async Task AuthoritativeEmptyRefreshReplacesPreviousValues()
    {
        var clock=new ManualClock(Start.AddHours(3));var empty=false;var history=new SalesService{ResultFactory=r=>empty?Result(r) with{ExportKwh=null,EnergyValuePln=null,EstimatedDepositPln=null,ObservedHours=0,ValuedHours=0,Buckets=[],DataError="Installation settings changed. Refresh the page."}:Result(r)};await using var services=Services(history,clock);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();empty=true;clock.Advance(TimeSpan.FromMinutes(5));await renderer.WaitForAsync(()=>renderer.Text(root).Contains("Installation settings changed"));Assert.DoesNotContain("sales-bar",renderer.Attributes(root,"data-testid"));Assert.DoesNotContain("5.00",renderer.Text(root));});
    }
    [Fact] public async Task AutomaticRefreshRecoversFromInitialError()
    {
        var clock=new ManualClock(Start.AddHours(3));var history=new SalesService{FailNext=true};await using var services=Services(history,clock);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();Assert.Contains("Export could not be loaded",renderer.Text(root));clock.Advance(TimeSpan.FromMinutes(5));await renderer.WaitForAsync(()=>renderer.Text(root).Contains("5.00"));Assert.Equal(2,history.Calls.Count);Assert.Equal(1,clock.ActiveTimers);});
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task WarsawMidnightFollowsTodayOnlyWithoutExplicitHistoricalDate(bool pinned)
    {
        var clock=new ManualClock(new(2026,9,30,21,58,0,TimeSpan.Zero));var history=new SalesService{ResultFactory=MultipleHours};await using var services=Services(history,clock);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>
        {
            var root=await renderer.MountAsync(pinned?"2026-09-29":null);
            clock.Advance(TimeSpan.FromMinutes(5));
            var expected=pinned?Today.AddDays(-1):Today.AddDays(1);
            await renderer.WaitForAsync(()=>history.Calls.Count==2&&renderer.Attributes(root,"value").Contains(expected.ToString("yyyy-MM-dd")));
            Assert.Equal(expected,history.Calls.Last().Request.Date);
            Assert.Contains("/energy?period=day&date="+expected.ToString("yyyy-MM-dd"),renderer.Attributes(root,"href"));
            Assert.Contains("/api/sales.csv?period=Day&date="+expected.ToString("yyyy-MM-dd")+"&includeUpcoming=true",renderer.Attributes(root,"href"));
        });
    }
    [Fact] public async Task AutomaticTimerDoesNotCancelOrDuplicateAnActiveSelection()
    {
        var clock=new ManualClock(Start.AddHours(3));var history=new SalesService{HoldHistorical=true};await using var services=Services(history,clock);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();var held=renderer.UpdateAsync(root,"2026-09-29");clock.Advance(TimeSpan.FromMinutes(5));await Task.Yield();Assert.Equal(2,history.Calls.Count);Assert.False(history.Calls.Last().Token.IsCancellationRequested);history.Complete(Today.AddDays(-1));await held;});
    }
    [Fact] public async Task FastSelectionCancelsAndFencesUncooperativeOlderResponse()
    {
        var history=new SalesService{HoldHistorical=true};await using var services=Services(history);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();var old=renderer.UpdateAsync(root,"2026-09-29");var latest=renderer.UpdateAsync(root,"2026-09-28");Assert.True(history.Calls[1].Token.IsCancellationRequested);history.Complete(Today.AddDays(-2));await renderer.WaitForAsync(()=>renderer.Attributes(root,"value").Contains("2026-09-28"));history.Complete(Today.AddDays(-1));await Task.WhenAll(latest,old);Assert.Equal(Today.AddDays(-2),history.Calls.Last().Request.Date);Assert.Contains("2026-09-28",renderer.Attributes(root,"value"));});
    }
    [Fact] public async Task DisposalStopsTimerCancelsRequestAndFencesLateCompletion()
    {
        var clock=new ManualClock(Start.AddHours(3));var history=new SalesService{HoldHistorical=true};await using var services=Services(history,clock);var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();var held=renderer.UpdateAsync(root,"2026-09-29");await renderer.DisposeAsync();Assert.True(history.Calls.Last().Token.IsCancellationRequested);Assert.Equal(0,clock.ActiveTimers);history.Complete(Today.AddDays(-1));await held;clock.Advance(TimeSpan.FromMinutes(10));Assert.Equal(2,history.Calls.Count);});
    }
    [Theory][InlineData("Day","day")][InlineData("7 days","7d")][InlineData("30 days","30d")][InlineData("Month","month")][InlineData("Custom","custom")]
    public async Task PeriodControlsKeepSelectionInRouteAndFetchOnlyAfterNavigation(string label,string period)
    {
        var history=new SalesService();await using var services=Services(history);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync();await renderer.ClickAsync(root,label);Assert.Contains("period="+period,services.GetRequiredService<NavigationManager>().Uri);Assert.Single(history.Calls);});
    }
    [Theory][InlineData("day","2026-09-30","2026-09-29")][InlineData("7d","2026-09-30","2026-09-23")][InlineData("30d","2026-09-30","2026-08-31")][InlineData("month","2026-09-01","2026-08-01")][InlineData("year","2026-01-01","2025-01-01")]
    public async Task PreviousPeriodUsesCalendarBoundaries(string period,string date,string previous)
    {
        await using var services=Services(new SalesService());await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync(date,period);await renderer.ClickAsync(root,"Previous period");Assert.Contains("date="+previous,services.GetRequiredService<NavigationManager>().Uri);});
    }
    [Theory][InlineData("day",null,"2026-10-01")][InlineData("7d","2026-09-30","2026-10-07")][InlineData("30d","2026-09-30","2026-10-30")][InlineData("month","2026-09-01","2026-10-01")]
    public async Task NextPeriodCanNavigateIntoFutureWithoutFetchingUntilNavigation(string period,string? date,string next)
    {
        var history=new SalesService();await using var services=Services(history);await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync(date,period);Assert.False(renderer.Control(root,"Next period").Disabled);await renderer.ClickAsync(root,"Next period");Assert.Contains("date="+next,services.GetRequiredService<NavigationManager>().Uri);Assert.Single(history.Calls);});
    }
    [Theory][InlineData("day","2027-10-01")][InlineData("7d","2027-10-01")][InlineData("30d","2027-10-01")][InlineData("month","2027-09-01")]
    public async Task NextPeriodStopsBeforeExceedingTheFutureRangeBound(string period,string date)
    {
        await using var services=Services(new SalesService());await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync(date,period);Assert.True(renderer.Control(root,"Next period").Disabled);});
    }
    [Fact]
    public async Task LegacyYearSelectionBecomesAnExplicitCompleteCustomCalendarWindow()
    {
        var history=new SalesService();await using var services=Services(history);
        var html=await RenderPage(services,new(){["RequestedPeriod"]="year",["RequestedDate"]="2026-09-30"});
        var request=Assert.Single(history.Calls).Request;
        Assert.Equal(ExportSalesPeriod.Custom,request.Period);
        Assert.Equal(new DateOnly(2026,1,1),request.From);
        Assert.Equal(new DateOnly(2026,12,31),request.Through);
        Assert.Contains("from=2026-01-01",html);Assert.Contains("through=2026-12-31",html);
    }
    [Theory][InlineData("banana",null,null,null)][InlineData("day","2026-02-30",null,null)][InlineData("day","2027-10-02",null,null)][InlineData("custom",null,"2026-09-30","2026-09-29")][InlineData("custom",null,"2025-01-01","2026-09-30")]
    public async Task InvalidSelectionNeverCallsSource(string period,string? date,string? from,string? to)
    {
        var history=new SalesService();await using var services=Services(history);var html=await RenderPage(services,new(){["RequestedDate"]=date,["RequestedPeriod"]=period,["RequestedFrom"]=from,["RequestedTo"]=to});Assert.Empty(history.Calls);Assert.Contains("valid period",html);
    }
    [Fact] public async Task CustomLimitsAreInclusiveAndCsvRetainsExactWindow()
    {
        var history=new SalesService();await using var services=Services(history);var html=await RenderPage(services,new(){["RequestedPeriod"]="custom",["RequestedFrom"]="2025-09-30",["RequestedTo"]="2026-09-30"});Assert.Single(history.Calls);Assert.Equal(365,history.Calls[0].Request.Through!.Value.DayNumber-history.Calls[0].Request.From!.Value.DayNumber);Assert.Contains("from=2025-09-30",html);Assert.Contains("through=2026-09-30",html);
    }
    private static ExportSalesResult Scenario(string scenario)=>scenario switch{
        "zero"=>Result() with{ExportKwh=0,CreditedExportKwh=0,EnergyValuePln=0,EstimatedDepositPln=0,Buckets=[new(Start,Start.AddHours(1),0,0,0,0,1,1,1)]},
        "partial"=>Result() with{ExpectedHours=2,ValuedHours=0,EnergyValuePln=null,EstimatedDepositPln=null,PriceError="The price has not been published yet.",Buckets=[new(Start,Start.AddHours(1),5,3,null,null,1,1,0),new(Start.AddHours(1),Start.AddHours(2),null,null,null,null,1,0,0)]},
        "error"=>Result() with{DataError="Deye history is unavailable.",ExportKwh=null,Buckets=[]},"empty"=>Result() with{ExportKwh=null,Buckets=[]},
        "before-contract"=>Result(new(ExportSalesPeriod.Day,Today.AddDays(-3))) with{ExpectedHours=0,ObservedHours=0,ValuedHours=0,ExportKwh=null,Buckets=[]},
        "month"=>Result(new(ExportSalesPeriod.Month,new(2026,9,1))),"year"=>Result(new(ExportSalesPeriod.Year,new(2026,1,1))),"custom"=>Result(new(ExportSalesPeriod.Custom,Today,Today.AddDays(-2),Today)),_=>Result()};
    private static ExportSalesResult Result(ExportSalesRequest? request=null)=>new(request??new(ExportSalesPeriod.Day,Today),Today,new(2026,9,28),"Europe/Warsaw",Start,Start.AddHours(1),[new(Start,Start.AddHours(1),5,3,1.2m,1.476m,1,1,1)],5,3,1.2m,1.476m,1,1,1);
    private static ExportSalesResult MultipleHours(ExportSalesRequest request){var at=Start.AddDays(request.Date.DayNumber-Today.DayNumber);return Result(request) with{Start=at,End=at.AddDays(1),ExpectedHours=3,ObservedHours=3,ValuedHours=3,ExportKwh=6,CreditedExportKwh=6,EnergyValuePln=3,EstimatedDepositPln=3.69m,Buckets=Enumerable.Range(0,3).Select(i=>new ExportSaleBucket(at.AddHours(i),at.AddHours(i+1),i+1,i+1,1,1.23m,1,1,1)).ToArray()};}
    private static ServiceProvider Services(IExportSalesService service,TimeProvider? clock=null){var s=new ServiceCollection();s.AddLogging();s.AddComponentLocalization();s.AddSingleton<IJSRuntime,NullJsRuntime>();s.AddSingleton<TimeProvider>(clock??new FixedClock());s.AddSingleton(service);s.AddSingleton<NavigationManager,TestNavigation>();s.AddSingleton<IOptionsMonitor<SolarSalesOptions>>(new FixedOptionsMonitor<SolarSalesOptions>(new(){TimeZoneId="Europe/Warsaw"}));return s.BuildServiceProvider();}
    private static async Task<string> RenderAsync(ExportSalesResult data){await using var services=Services(new SalesService{ResultFactory=_=>data});return await RenderPage(services,new(){["RequestedPeriod"]=data.Request.Period.ToString(),["RequestedDate"]=data.Request.Date.ToString("yyyy-MM-dd"),["RequestedFrom"]=data.Request.From?.ToString("yyyy-MM-dd"),["RequestedTo"]=data.Request.Through?.ToString("yyyy-MM-dd")});}
    private static async Task<string> RenderPage(IServiceProvider services,Dictionary<string,object?> args){await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());return await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<ExportHost>(ParameterView.FromDictionary(args))).ToHtmlString()));}
    private sealed class TestNavigation:NavigationManager{public TestNavigation()=>Initialize("http://localhost/","http://localhost/energy/export");protected override void NavigateToCore(string uri,bool forceLoad)=>Uri=ToAbsoluteUri(uri).ToString();}
    // Query values normally come from the router. Forward identical inputs to the real page lifecycle.
    private sealed class ExportHost:EnergyExport{
        [Parameter]public string? RequestedDate{get;set;}[Parameter]public string? RequestedPeriod{get;set;}[Parameter]public string? RequestedFrom{get;set;}[Parameter]public string? RequestedTo{get;set;}
        protected override Task OnParametersSetAsync(){DateQuery=RequestedDate;PeriodQuery=RequestedPeriod;FromQuery=RequestedFrom;ToQuery=RequestedTo;return base.OnParametersSetAsync();}}
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;
        public int ActiveTimers => _timers.Count(timer => !timer.Disposed);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            var target = _now + duration;
            while (_timers.Where(timer => !timer.Disposed && timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault() is { } next)
            {
                _now = next.Due;
                next.Fire();
            }
            _now = target;
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public DateTimeOffset Due { get; private set; }
            private TimeSpan _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                _period = period;
                return true;
            }
            public void Fire()
            {
                Due = _period <= TimeSpan.Zero ? DateTimeOffset.MaxValue : Due + _period;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class SalesService : IExportSalesService
    {
        public List<(ExportSalesRequest Request, CancellationToken Token)> Calls { get; } = [];
        public bool HoldHistorical { get; init; }
        public bool MissingPrices { get; init; }
        public bool SignedNegativePrices { get; init; }
        public bool FailNext { get; set; }
        public bool HoldNext { get; set; }
        public Func<ExportSalesRequest, ExportSalesResult>? ResultFactory { get; init; }
        private readonly Dictionary<DateOnly, TaskCompletionSource<ExportSalesResult>> _pending = [];
        public Task<ExportSalesResult> ReadAsync(ExportSalesRequest request, CancellationToken ct)
        {
            Calls.Add((request, ct));
            if (FailNext) { FailNext = false; throw new InvalidOperationException("A transient data-source failure."); }
            if (HoldNext || HoldHistorical && request.Date < Today)
            {
                HoldNext = false;
                // Complete canceled requests deliberately to exercise late-response fencing.
                var pending = new TaskCompletionSource<ExportSalesResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(request.Date, pending);
                return pending.Task;
            }
            var result = ResultFactory?.Invoke(request) ?? Result(request);
            if (MissingPrices) result = result with
            {
                EnergyValuePln = null, EstimatedDepositPln = null, ValuedHours = 0,
                Buckets = result.Buckets.Select(bucket => bucket with { EnergyValuePln = null, EstimatedDepositPln = null, ValuedHours = 0 }).ToArray()
            };
            if (SignedNegativePrices) result = result with
            {
                EnergyValuePln = -0.6m, EstimatedDepositPln = -0.738m,
                Buckets = result.Buckets.Select(bucket => bucket with { EnergyValuePln = -0.6m, EstimatedDepositPln = -0.738m }).ToArray()
            };
            return Task.FromResult(result);
        }
        public void Complete(DateOnly date)
        {
            var request = Calls.Last(call => call.Request.Date == date).Request;
            _pending[date].SetResult(ResultFactory?.Invoke(request) ?? Result(request));
        }
    }

    // Dispatch actual Blazor events so navigation and asynchronous fencing are tested through the UI.
    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        private TaskCompletionSource _displayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            var previous = _displayChanged;
            _displayChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
            return Task.CompletedTask;
        }
        public async Task WaitForAsync(Func<bool> condition)
        {
            while (!condition()) await _displayChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        public async Task<int> MountAsync(string? date = null, string? period = null)
        {
            var root = AssignRootComponentId(InstantiateComponent(typeof(ExportHost)));
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?> { ["RequestedDate"] = date, ["RequestedPeriod"] = period }));
            return root;
        }
        public Task UpdateAsync(int root, string? date = null, string? period = null, string? from = null, string? to = null) => RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string,object?> { ["RequestedDate"] = date, ["RequestedPeriod"] = period, ["RequestedFrom"] = from, ["RequestedTo"] = to }));
        public Task KeyAsync(int root, string key) => DispatchEventAsync(Event(root,"onkeydown"),null,new KeyboardEventArgs{Key=key});
        private ulong Event(int root,string name){var frames=GetCurrentRenderTreeFrames(root);foreach(var f in frames.Array.Take(frames.Count)){if(f.FrameType==RenderTreeFrameType.Component){var nested=Event(f.ComponentId,name);if(nested!=0)return nested;}if(f.FrameType==RenderTreeFrameType.Attribute&&f.AttributeName==name)return f.AttributeEventHandlerId;}return 0;}
        public IEnumerable<string?> Attributes(int componentId, string name)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            foreach (var frame in frames.Array.Take(frames.Count))
            {
                if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name) yield return frame.AttributeValue?.ToString();
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var value in Attributes(frame.ComponentId, name)) yield return value;
            }
        }
        public Task ClickAsync(int root, string label)
        {
            var control = Control(root, label);
            Assert.False(control.Disabled);
            Assert.NotEqual(0UL, control.EventId);
            return DispatchEventAsync(control.EventId, null, new MouseEventArgs());
        }
        public Task ChangeAsync(int root, string label, string value)
        {
            var control = Assert.Single(Controls(root), item => item.Label == label && item.Element == "input");
            Assert.NotEqual(0UL, control.EventId);
            return DispatchEventAsync(control.EventId, null, new ChangeEventArgs { Value = value });
        }
        public record ControlInfo(string Element, string Label, bool Disabled, ulong EventId);
        public ControlInfo Control(int root, string label) => Assert.Single(Controls(root), control => control.Label == label && control.Element == "button");
        private IEnumerable<ControlInfo> Controls(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var control in Controls(frame.ComponentId)) yield return control;
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName is not ("button" or "input")) continue;
                string? label = null;
                var disabled = false;
                var eventId = 0UL;
                for (var j = i + 1; j < i + frame.ElementSubtreeLength; j++)
                {
                    var child = frames.Array[j];
                    if (child.FrameType != RenderTreeFrameType.Attribute) continue;
                    if (child.AttributeName == "aria-label") label = child.AttributeValue?.ToString();
                    if (child.AttributeName == "disabled") disabled = child.AttributeValue is true;
                    if (child.AttributeName is "onclick" or "onchange") eventId = child.AttributeEventHandlerId;
                }
                label ??= string.Concat(frames.Array.Skip(i + 1).Take(frame.ElementSubtreeLength - 1).Select(child => child.FrameType switch
                {
                    RenderTreeFrameType.Text => child.TextContent,
                    RenderTreeFrameType.Markup => WebUtility.HtmlDecode(Regex.Replace(child.MarkupContent, "<[^>]*>", "")),
                    _ => ""
                }));
                yield return new(frame.ElementName, label, disabled, eventId);
            }
        }
        public string Text(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            return string.Join(" ", frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Text => frame.TextContent,
                RenderTreeFrameType.Markup => frame.MarkupContent,
                RenderTreeFrameType.Component => Text(frame.ComponentId),
                RenderTreeFrameType.Attribute when frame.AttributeName is "aria-label" or "title" => frame.AttributeValue?.ToString(),
                _ => ""
            }));
        }
    }

    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
