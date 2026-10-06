using System.Globalization;
using DeyeSolar.Web.Components.Charts;
using DeyeSolar.Web.Components.Ui;
using Microsoft.AspNetCore.Http;
using Xunit;
namespace DeyeSolar.Web.Tests;
public class DesignGeometryTests
{
    [Fact] public void MissingAndInvalidSamplesStartNewSegmentsWhileZeroIsMeasured()
    {
        double?[] values=[0,2,null,4,double.NaN,0];
        Assert.Equal(new[]{(0,1),(3,3),(5,5)},ChartGeometry.Segments(values));
        var path=ChartGeometry.Line(values,100,50,5);
        Assert.Equal(3,path.Count(c=>c=='M'));Assert.DoesNotContain("NaN",path);Assert.Contains("M 0 50",path);
    }
    [Fact] public void RangeGapsAreNotBridgedAndMismatchedRangesAreRejected()
    {
        var path=ChartGeometry.Band([0,null,1],[2,3,4],100,50,5);
        Assert.Equal(2,path.Count(c=>c=='M'));Assert.Equal(2,path.Count(c=>c=='Z'));
        Assert.Throws<ArgumentException>(()=>ChartGeometry.Band([1],[2,3],100,50,5));
    }
    [Fact] public void GeometryRemainsInvariantAndSelectionClampsToData()
    {
        var culture=CultureInfo.CurrentCulture;try { CultureInfo.CurrentCulture=new("pl-PL");Assert.Equal("1.25",ChartGeometry.Number(1.25)); } finally {CultureInfo.CurrentCulture=culture;}
        Assert.Equal(0,ChartGeometry.Select(-20,100,5));Assert.Equal(4,ChartGeometry.Select(200,100,5));Assert.Equal(-1,ChartGeometry.Select(20,100,0));
    }
    [Fact] public void ExplicitThemeCookieRejectsUnknownValues()
    {
        var context=new DefaultHttpContext();context.Request.Headers.Cookie="deyeSolarAppearance=dark";Assert.Equal("dark",WebAppearance.CookieTheme(context));
        context.Request.Headers.Cookie="deyeSolarAppearance=unsafe";Assert.Equal("light",WebAppearance.CookieTheme(context));
    }
    [Fact] public void LocalizedNumbersAndOptionalTimesKeepTheirMeaning()
    {
        Assert.True(UiInputParser.TryNumber("12,5",new CultureInfo("pl-PL"),out var number));Assert.Equal(12.5,number);
        Assert.False(UiInputParser.TryNumber("NaN",CultureInfo.InvariantCulture,out _));
        Assert.True(UiInputParser.TryTime("",out var empty));Assert.Null(empty);
        Assert.True(UiInputParser.TryTime("23:59",out var time));Assert.Equal(new TimeSpan(23,59,0),time);
        Assert.False(UiInputParser.TryTime("24:00",out _));
    }
}
