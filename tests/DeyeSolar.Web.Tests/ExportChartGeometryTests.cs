using DeyeSolar.Web.Components.Charts;

namespace DeyeSolar.Web.Tests;

public sealed class ExportChartGeometryTests
{
    [Fact]
    public void MissingBarsRemainAbsentWhileZeroHasAVisibleBaseline()
    {
        var scale = ExportChartGeometry.Scale([(null, null), (0, null)]);
        Assert.Null(scale.Bar(null));
        Assert.Equal(1, scale.Bar(0)!.Value.Height);
        Assert.Equal(scale.Y(0), scale.Bar(0)!.Value.Top);
        Assert.True(double.IsFinite(scale.Y(0)));
    }
    [Theory]
    [InlineData(2, -1)]
    [InlineData(-2, 1)]
    [InlineData(-2, -1)]
    public void SignedProgressRetainsBothItsOriginAndEndpoint(double completed, double progress)
    {
        var scale = ExportChartGeometry.Scale([((decimal)completed, (decimal)progress)]);
        foreach (var value in new[] { 0, completed, completed + progress })
            Assert.InRange(scale.Y(value), 28, 232);
        var bar = scale.Bar((decimal)progress, (decimal)completed)!.Value;
        Assert.InRange(bar.Top, 28, 232);
        Assert.InRange(bar.Top + bar.Height, 28, 232);
    }
    [Fact]
    public void ZeroAndEmptyAxesNeverProduceNonfiniteCoordinates()
    {
        var scale = ExportChartGeometry.Scale([]);
        Assert.True(double.IsFinite(scale.Y(0)));
        Assert.Equal(472, ExportChartGeometry.X(0, 1));
        Assert.True(ExportChartGeometry.Step(0) > 0);
    }
}
