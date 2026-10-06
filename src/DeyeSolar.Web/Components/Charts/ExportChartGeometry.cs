namespace DeyeSolar.Web.Components.Charts;

/// <summary>Pure chart projection. Missing samples only affect the axis baseline, never create bars.</summary>
public readonly record struct ExportChartScale(double Minimum, double Maximum)
{
    public double Y(double value) => 232 - (value - Minimum) / (Maximum - Minimum) * 204;
    public (double Top, double Height)? Bar(decimal? value, decimal baseline = 0)
    {
        if (value is null) return null;
        var a = Y((double)baseline);
        var b = Y((double)(baseline + value.Value));
        return (Math.Min(a, b), Math.Max(1, Math.Abs(a - b)));
    }
}

public static class ExportChartGeometry
{
    public static ExportChartScale Scale(IEnumerable<(decimal? Completed, decimal? Progress)> samples)
    {
        // Include both endpoints of each provisional increment: a negative increment
        // must not crop the completed amount it starts from.
        var values = samples.SelectMany(sample => new[] { (double)(sample.Completed ?? 0),
            (double)((sample.Completed ?? 0) + (sample.Progress ?? 0)) }).ToArray();
        return new(Math.Min(0, values.DefaultIfEmpty(0).Min()),
            Math.Max(.1, values.DefaultIfEmpty(0).Max()) * 1.12);
    }
    public static double Step(int count) => 848d / Math.Max(1, count);
    public static double X(int index, int count) => 48 + (index + .5) * Step(count);
}
