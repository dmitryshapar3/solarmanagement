using System.Globalization;
using System.Net;
using DeyeSolar.Web.Components.Charts;
using DeyeSolar.Web.Redesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace DeyeSolar.Web.Tests;
public class ReadingsPlotTests
{
    [Fact]
    public async Task RenderedAxesAreFormattedNumbersAndSourceChangesDoNotConnectTheCurve()
    {
        var now=new DateTimeOffset(2026,10,6,12,0,0,TimeSpan.Zero);
        var source=Guid.NewGuid();
        ReadingRowDto Row(int id,int minute,long generation,double solar)=>new(id,now.AddMinutes(minute),source,1,generation,"measured",70,null,null,null,null,solar,null,null,now.AddMinutes(minute));
        var view=new ReadingsViewDto(now,now.AddMinutes(30),"raw",[Row(1,0,1,0),Row(2,5,1,4000),Row(3,10,2,2000)],[],null,true);
        var services=new ServiceCollection();services.AddLogging();services.AddComponentLocalization();
        await using var provider=services.BuildServiceProvider();
        await using var renderer=new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
        var html=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<ReadingsPlot>(ParameterView.FromDictionary(new Dictionary<string,object?>{{"Data",view},{"TimeZoneId","UTC"}}))).ToHtmlString()));
        Assert.DoesNotContain("ToString",html);
        Assert.Contains(">4.48</text>",html);
        Assert.Contains(">100%</text>",html);
        // Each series starts again for a new runtime generation. An isolated measurement stays visible.
        Assert.Equal(4,System.Text.RegularExpressions.Regex.Matches(html,"<path ").Count);
        Assert.Equal(2,System.Text.RegularExpressions.Regex.Matches(html,"<circle ").Count);
    }
}
