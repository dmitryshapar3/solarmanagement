using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeyeSolar.Domain.Interfaces;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Components.Charts;
using DeyeSolar.Web.Pages;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Services;
using DeyeSolar.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SolarManagement.Inverters.Contracts;
using Basis = DeyeSolar.Domain.Models.SolarPowerBasis;

namespace DeyeSolar.Web.Tests;

public class SolarHistoryChartTests
{
    private static readonly DateTimeOffset Start = new(2026,9,29,6,0,0,TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026,9,29);
    private static ProductionViewDto Data(params (double? Lower,double? Upper,double? Actual)[] points) => new(Start,Start.AddHours(points.Length),"UTC",Today,Today,
        points.Select((p,i)=>new ProductionHourDto(Start.AddHours(i),p.Actual,p.Actual, p.Actual.HasValue?3600:0,3600,p.Lower,p.Lower,p.Upper,!p.Actual.HasValue)).ToArray(),[],null,null,0,0,null,null,null,null,null,null,null,null,null,true);
    [Theory]
    [InlineData(false,"normal")][InlineData(true,"normal")][InlineData(true,"zero")][InlineData(true,"missing")][InlineData(false,"weather-error")][InlineData(false,"actual-error")][InlineData(true,"empty")]
    public async Task NativeChartPreservesAvailableSeriesAndAccessiblePowerUnits(bool compact,string scenario)
    {
        var data=scenario switch{"zero"=>Data((0,0,0)),"missing"=>Data((null,null,null)),"empty"=>Data(),"weather-error"=>Data((null,null,2)) with{WeatherError="Weather data is unavailable."},"actual-error"=>Data((1,3,null)) with{ActualError="Inverter history is unavailable."},_=>Data((1.2,3.4,2.1),(1.3,3.5,2.2))};
        var html=await RenderAsync(data,compact:compact);
        Assert.Contains("role=\"img\"",html);Assert.Contains("Use left and right arrow keys",html);Assert.DoesNotContain("Refresh chart",html);
        Assert.DoesNotMatch("[\\u0400-\\u04FF]",html);
        var actual=Path(html,"actual-series");var range=Path(html,"possible-band");
        Assert.Equal(scenario is "missing" or "empty" or "actual-error",actual.Length==0);
        Assert.Equal(scenario is "missing" or "empty" or "weather-error",range.Length==0);
        if(!compact){Assert.Contains("Expected range",html);Assert.Contains("kW",html);}
    }
    [Fact] public async Task RangeUsesBothBoundsAndItsUpperBoundControlsTheScale()
    {
        var html=await RenderAsync(Data((1,6,.5),(2,5,1)));var vertices=Vertices(Path(html,"possible-band"));
        Assert.Equal(4,vertices.Length);Assert.True(vertices.Min(v=>v.Y)<vertices.Max(v=>v.Y));Assert.Contains("1.00–6.00",html);
        Assert.DoesNotContain("central-line",html);Assert.Contains("Expected range",html);
    }
    [Fact] public async Task MissingHoursSplitForecastAndActualIndependently()
    {
        var html=await RenderAsync(Data((1,2,.8),(null,null,1.1),(2,3,null),(3,4,2.2),(4,5,2.6)));
        Assert.Equal(2,Regex.Matches(Path(html,"possible-band"),"M ").Count);Assert.Equal(2,Regex.Matches(Path(html,"actual-series"),"M ").Count);
    }
    [Fact] public async Task IsolatedRangeKeepsItsOwnNarrowColumnAndBothBounds()
    {
        var vertices=Vertices(Path(await RenderAsync(Data((null,null,null),(1,3,null),(null,null,null))),"possible-band"));
        Assert.Equal(4,vertices.Length);Assert.InRange(vertices.Max(v=>v.X)-vertices.Min(v=>v.X),1,16);Assert.Equal(2,vertices.Select(v=>v.Y).Distinct().Count());
    }
    [Fact] public async Task MeasuredZeroRendersAtTheBaselineInsteadOfBecomingMissing()
    {
        var html=await RenderAsync(Data((0,0,0)));Assert.Contains("0.00 kW",html);Assert.Equal(204,Assert.Single(Vertices(Path(html,"actual-series"))).Y);Assert.NotEmpty(Path(html,"possible-band"));Assert.DoesNotContain("NaN",html);
    }
    [Fact] public async Task PolishNumbersDoNotChangeSvgCoordinates()
    {
        var previous=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("pl-PL");var html=await RenderAsync(Data((1.3,2.1,2.8),(2.5,3.3,1.6)));Assert.DoesNotContain(",",Path(html,"possible-band"));Assert.DoesNotContain(",",Path(html,"actual-series"));Assert.Contains("1,30–2,10",html);Assert.NotEmpty(Vertices(Path(html,"possible-band")));}finally{CultureInfo.CurrentCulture=previous;}
    }
    [Fact] public async Task ChartSelectionChangesOnlyTheInspectorAndNeverFetchesOrRelabelsTheLoadedDate()
    {
        await using var services=Basic(new Clock());await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync<PlotHost>();Assert.Contains("06:00",renderer.Text(root));await renderer.KeyAsync(root,"ArrowRight");Assert.Contains("07:00",renderer.Text(root));await renderer.KeyAsync(root,"ArrowRight");Assert.Contains("07:00",renderer.Text(root));await renderer.KeyAsync(root,"ArrowLeft");Assert.Contains("06:00",renderer.Text(root));});
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(29)][InlineData(-15)]
    public async Task EnergyPageShowsTheSelectedDateAndAllowsPastAndFutureNavigation(int daysAgo)
    {
        var previousCulture=CultureInfo.CurrentCulture;
        var previousUiCulture=CultureInfo.CurrentUICulture;
        var culture=CultureInfo.GetCultureInfo("en-GB");
        try
        {
            CultureInfo.CurrentCulture=culture;
            CultureInfo.CurrentUICulture=culture;
            var f=new Fixture();await using var services=f.Services();await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async()=>
            {
                var date=Today.AddDays(-daysAgo);
                var root=await renderer.MountAsync<EnergyHost>(new(){["RequestedDate"]=date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)});
                Assert.Contains(date.ToString("d MMM yyyy",culture),renderer.Text(root));
                Assert.False(renderer.Button(root,"Next period").Disabled);
                Assert.False(renderer.Button(root,"Previous period").Disabled);
            });
        }
        finally
        {
            CultureInfo.CurrentCulture=previousCulture;
            CultureInfo.CurrentUICulture=previousUiCulture;
        }
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task SourceFailureKeepsTheOtherSeriesAndDisclosesUnavailableData(bool weather)
    {
        var f=new Fixture();f.Weather.Fail=weather;f.Store.Fail=!weather;await using var services=f.Services();var html=await RenderPageAsync(services);
        Assert.Contains(weather?"Weather data is unavailable.":"Inverter history is unavailable.",html);Assert.NotEmpty(Path(html,weather?"actual-series":"possible-band"));Assert.Empty(Path(html,weather?"possible-band":"actual-series"));Assert.Contains("Gaps stay gaps",html);
    }
    [Fact] public async Task MidnightDoesNotRelabelLoadedDataUntilNewParametersLoad()
    {
        var f=new Fixture();await using var services=f.Services();await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync<EnergyHost>();f.Clock.Now=f.Clock.Now.AddDays(1);Assert.Contains("29 Sep 2026",renderer.Text(root));Assert.DoesNotContain("30 Sep 2026",renderer.Text(root));await renderer.DateAsync(root,Today.AddDays(1));Assert.Contains("30 Sep 2026",renderer.Text(root));});
    }
    [Fact] public async Task FasterDateSelectionCancelsAndFencesTheOldResponse()
    {
        var f=new Fixture();f.Store.HoldPast=true;await using var services=f.Services();await using var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync<EnergyHost>();var old=renderer.DateAsync(root,Today.AddDays(-1));var latest=renderer.DateAsync(root,Today.AddDays(-2));Assert.True(f.Store.Pending[Today.AddDays(-1)].Token.IsCancellationRequested);var updated=renderer.NextRender();f.Store.Complete(Today.AddDays(-2));await updated.WaitAsync(TimeSpan.FromSeconds(5));Assert.Contains("27 Sep 2026",renderer.Text(root));f.Store.Complete(Today.AddDays(-1));await Task.WhenAll(latest,old).WaitAsync(TimeSpan.FromSeconds(5));Assert.Contains("27 Sep 2026",renderer.Text(root));Assert.DoesNotContain("28 Sep 2026",renderer.Text(root));});
    }
    [Fact] public async Task DisposalCancelsOutstandingProductionAndRejectsLateResponses()
    {
        var f=new Fixture();f.Store.HoldPast=true;await using var services=f.Services();var renderer=new EventRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>{var root=await renderer.MountAsync<EnergyHost>();var pending=renderer.DateAsync(root,Today.AddDays(-1));await renderer.DisposeComponentAsync(root);Assert.True(f.Store.Pending[Today.AddDays(-1)].Token.IsCancellationRequested);f.Store.Complete(Today.AddDays(-1));await pending.WaitAsync(TimeSpan.FromSeconds(5));await renderer.DisposeAsync();});
    }
    [Fact] public async Task EmptyActualHoursStayMissingAndRetainForecastAndDayNavigation()
    {
        var f=new Fixture();f.Store.Empty=true;await using var services=f.Services();var html=await RenderPageAsync(services);Assert.Empty(Path(html,"actual-series"));Assert.NotEmpty(Path(html,"possible-band"));Assert.Contains("Forecast",html);Assert.Contains("Partial coverage",html);Assert.Contains("A dash means unavailable; measured zero remains 0.00.",html);Assert.Contains("Previous period",html);
    }
    [Theory][InlineData("999","2026-09-29")][InlineData("day","2026-02-30")]
    public async Task InvalidPeriodOrDateIsRejectedWithoutReadingSources(string period,string date)
    {
        var f=new Fixture();await using var services=f.Services();var html=await RenderPageAsync(services,new(){["RequestedPeriod"]=period,["RequestedDate"]=date});Assert.Contains("Choose a valid period of at most 366 days.",html);Assert.Equal(0,f.Store.Calls);Assert.Equal(0,f.Weather.Calls);
    }
    private static async Task<string> RenderPageAsync(IServiceProvider services,Dictionary<string,object?>? parameters=null)
    {await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());return await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<EnergyHost>(ParameterView.FromDictionary(parameters??[]))).ToHtmlString()));}
    private static async Task<string> RenderAsync(ProductionViewDto data,bool compact=false)
    {await using var services=Basic(new Clock());await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());return await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<ProductionPlot>(ParameterView.FromDictionary(new Dictionary<string,object?>{["Data"]=data,["Compact"]=compact}))).ToHtmlString()));}
    private static string Path(string html,string id){var tag=Regex.Match(html,"<path\\b[^>]*data-testid=\""+id+"\"[^>]*/?>").Value;Assert.NotEmpty(tag);return Regex.Match(tag,"\\bd=\"([^\"]*)\"").Groups[1].Value;}
    private static (double X,double Y)[] Vertices(string path)=>Regex.Matches(path,"[ML]\\s+(-?\\d+(?:\\.\\d+)?)\\s+(-?\\d+(?:\\.\\d+)?)").Select(m=>(double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture),double.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture))).ToArray();
    // The router supplies query values as cascades; this host forwards the same public
    // inputs to the real page lifecycle without changing its rendering or query code.
    private sealed class EnergyHost:Energy
    {
        [Parameter] public string? RequestedDate {get;set;}
        [Parameter] public string? RequestedPeriod {get;set;}
        protected override Task OnParametersSetAsync(){DateQuery=RequestedDate;PeriodQuery=RequestedPeriod;return base.OnParametersSetAsync();}
    }
    private sealed class PlotHost:ComponentBase
    {private int _selected;protected override void BuildRenderTree(RenderTreeBuilder b){b.OpenComponent<ProductionPlot>(0);b.AddAttribute(1,"Data",Data((1,2,.8),(3,4,2.2)));b.AddAttribute(2,"SelectedIndex",_selected);b.AddAttribute(3,"SelectedIndexChanged",EventCallback.Factory.Create<int>(this,(int value)=>_selected=value));b.CloseComponent();}}
    private sealed class Clock:TimeProvider{public DateTimeOffset Now=Start.AddHours(5);public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Navigation:NavigationManager{public Navigation()=>Initialize("http://localhost/","http://localhost/energy");protected override void NavigateToCore(string uri,bool forceLoad){}}
    private static ServiceProvider Basic(Clock clock)=>Base(clock).BuildServiceProvider();
    private static ServiceCollection Base(Clock clock){var s=new ServiceCollection();s.AddLogging();s.AddComponentLocalization();s.AddSingleton<TimeProvider>(clock);s.AddSingleton<IJSRuntime,NullJsRuntime>();s.AddSingleton<NavigationManager,Navigation>();return s;}
    private sealed class Fixture
    {
        public Clock Clock=new();public Forecast Weather=new();public History Store=new();
        public ServiceProvider Services(){var s=Base(Clock);s.AddOptions<SolarEstimateOptions>().Configure(o=>{o.TimeZoneId="UTC";o.DeyeSolarPowerIsPvDcConfirmed=true;o.DeyeConfirmedDeviceSn="primary";});s.Configure<InverterConnectionOptions>(o=>o.DeviceKey="primary");s.AddSingleton<ISolarDayForecastSource>(Weather);s.AddSingleton<ISolarHistoryStore>(Store);s.AddSingleton<SolarProductionService>();s.AddSingleton<InverterDataSnapshot>();s.AddSingleton<ISolarRadiationSource,UnusedSource>();s.AddSingleton<ISolarEstimateStore,UnusedStore>();s.AddSingleton<SolarEstimateService>();s.AddSingleton<IInverterRefreshService,UnusedRefresh>();return s.BuildServiceProvider();}
    }
    private sealed class Forecast:ISolarDayForecastSource
    {public bool Fail;public int Calls;public Task<SolarDayForecast> ReadAsync(SolarEstimateOptions o,DateTimeOffset a,DateTimeOffset b,DateOnly date,CancellationToken ct){Calls++;if(Fail)throw new HttpRequestException();return Task.FromResult(new SolarDayForecast(Enumerable.Range(0,(int)(b-a).TotalHours).Select(i=>new SolarWeatherSample(a.AddHours(i),500,500,20,1,0)).ToArray(),Start,null,null,null));}}
    private sealed class History:ISolarHistoryStore
    {public bool Fail,Empty,HoldPast;public int Calls;public Dictionary<DateOnly,(TaskCompletionSource<IReadOnlyList<SolarActual>> Response,CancellationToken Token)> Pending=[];
     public Task<IReadOnlyList<SolarActual>> ReadAsync(string device,DateTimeOffset a,DateTimeOffset b,CancellationToken ct){Calls++;if(Fail)throw new HttpRequestException();var date=DateOnly.FromDateTime(a.AddMinutes(10).UtcDateTime);if(HoldPast&&date<Today){var response=new TaskCompletionSource<IReadOnlyList<SolarActual>>(TaskCreationOptions.RunContinuationsAsynchronously);Pending.Add(date,(response,ct));return response.Task;}return Task.FromResult(Samples(date));}
     private IReadOnlyList<SolarActual> Samples(DateOnly date)=>Empty?[]:Enumerable.Range(0,37).Select(i=>new SolarActual(new DateTimeOffset(date.ToDateTime(new TimeOnly(6,0)),TimeSpan.Zero).AddMinutes(i*5),2,Basis.PvDc)).ToArray();
     public void Complete(DateOnly date)=>Pending[date].Response.SetResult(Samples(date));}
    private sealed class UnusedSource:ISolarRadiationSource{public Task<SolarRadiationObservation> ReadAsync(SolarEstimateOptions o,DateTimeOffset n,CancellationToken ct)=>throw new InvalidOperationException("No live weather fetch from supplied snapshot");}
    private sealed class UnusedStore:ISolarEstimateStore{public Task<CachedSolarObservation?> LoadAsync(CancellationToken ct)=>throw new NotSupportedException();public Task SaveAsync(CachedSolarObservation o,CancellationToken ct)=>throw new NotSupportedException();public Task<SolarActual?> FindActualAsync(DateTimeOffset t,int tolerance,DateTimeOffset n,CancellationToken ct)=>throw new NotSupportedException();}
    private sealed class UnusedRefresh:IInverterRefreshService{public Task<InverterData> RefreshAsync(CancellationToken ct)=>throw new InvalidOperationException("UI must not refresh hardware on load");}
    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        private readonly Dictionary<int,IComponent> _mounted=[];
        public ValueTask DisposeComponentAsync(int root)=>((IAsyncDisposable)_mounted[root]).DisposeAsync();
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private TaskCompletionSource? _nextRender;
        public Task NextRender()=> (_nextRender=new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch){_nextRender?.TrySetResult();_nextRender=null;return Task.CompletedTask;}
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();

        public async Task<int> MountAsync<T>(Dictionary<string, object?>? parameters = null) where T : IComponent
        {
            var component=InstantiateComponent(typeof(T));
            var root = AssignRootComponentId(component);
            _mounted[root]=component;
            await RenderRootComponentAsync(root, ParameterView.FromDictionary(parameters ?? []));
            return root;
        }
        public Task DateAsync(int root, DateOnly date) => RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?> { ["RequestedDate"] = date.ToString("yyyy-MM-dd"), ["RequestedPeriod"] = "day" }));
        public Task KeyAsync(int root, string key) => DispatchEventAsync(Event(root, "onkeydown"), null, new KeyboardEventArgs { Key = key });
        private ulong Event(int root, string name)
        {
            var frames = GetCurrentRenderTreeFrames(root);
            foreach (var frame in frames.Array.Take(frames.Count))
            {
                if (frame.FrameType == RenderTreeFrameType.Component) { var nested = Event(frame.ComponentId, name); if (nested != 0) return nested; }
                if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name) return frame.AttributeEventHandlerId;
            }
            return 0;
        }

        public Task ClickAsync(int root, string label)
        {
            var button = Button(root, label);
            Assert.False(button.Disabled, "Button is disabled: " + label);
            Assert.NotEqual(0UL, button.EventId);
            return DispatchEventAsync(button.EventId, null, new MouseEventArgs());
        }

        public record ButtonInfo(string Label, bool Disabled, ulong EventId);
        public ButtonInfo Button(int root, string label) => Assert.Single(Buttons(root), button => button.Label == label);

        private IEnumerable<ButtonInfo> Buttons(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var button in Buttons(frame.ComponentId)) yield return button;
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                string? label = null;
                var disabled = false;
                var eventId = 0UL;
                for (var j = i + 1; j < i + frame.ElementSubtreeLength; j++)
                {
                    var child = frames.Array[j];
                    if (child.FrameType != RenderTreeFrameType.Attribute) continue;
                    if (child.AttributeName == "aria-label") label = child.AttributeValue?.ToString();
                    if (child.AttributeName == "disabled") disabled = child.AttributeValue is true;
                    if (child.AttributeName == "onclick") eventId = child.AttributeEventHandlerId;
                }
                label ??= string.Concat(frames.Array.Skip(i + 1).Take(frame.ElementSubtreeLength - 1).Select(child => child.FrameType switch
                {
                    RenderTreeFrameType.Text => child.TextContent,
                    RenderTreeFrameType.Markup => WebUtility.HtmlDecode(Regex.Replace(child.MarkupContent, "<[^>]*>", "")),
                    _ => ""
                }));
                yield return new(label, disabled, eventId);
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
