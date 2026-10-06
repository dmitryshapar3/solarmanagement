using System.Globalization;
using System.Text;
namespace DeyeSolar.Web.Components.Charts;

public static class ChartGeometry
{
    public static string Number(double value) => double.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(value));
    public static double X(int index, int count, double width) => count < 2 ? width / 2 : index * width / (count - 1);
    public static double Y(double value, double min, double max, double height) => height - (value - min) / Math.Max(double.Epsilon, max - min) * height;
    public static IReadOnlyList<(int Start, int End)> Segments(IReadOnlyList<double?> values)
    {
        var result = new List<(int, int)>();
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value || !double.IsFinite(value)) continue;
            var start = i;
            while (i + 1 < values.Count && values[i + 1] is { } next && double.IsFinite(next)) i++;
            result.Add((start, i));
        }
        return result;
    }
    public static string Line(IReadOnlyList<double?> values, double width, double height, double max, double min = 0)
    {
        if (!(width > 0 && height > 0 && max > min)) throw new ArgumentOutOfRangeException(nameof(width));
        var output = new StringBuilder();
        foreach (var (start, end) in Segments(values))
        {
            output.Append($"M {Number(X(start, values.Count, width))} {Number(Y(values[start]!.Value, min, max, height))} ");
            for (var i = start; i < end; i++)
            {
                var x1 = X(i, values.Count, width); var x2 = X(i + 1, values.Count, width);
                var y1 = Y(values[i]!.Value, min, max, height); var y2 = Y(values[i + 1]!.Value, min, max, height);
                var before = Y(values[Math.Max(start, i - 1)]!.Value, min, max, height);
                var after = Y(values[Math.Min(end, i + 2)]!.Value, min, max, height);
                // Clipped Catmull-Rom controls cannot loop beyond a segment or overshoot its extrema.
                var c1 = Math.Clamp(y1 + (y2 - before) / 6, Math.Min(y1, y2), Math.Max(y1, y2));
                var c2 = Math.Clamp(y2 - (after - y1) / 6, Math.Min(y1, y2), Math.Max(y1, y2));
                output.Append($"C {Number(x1 + (x2 - x1) / 3)} {Number(c1)} {Number(x2 - (x2 - x1) / 3)} {Number(c2)} {Number(x2)} {Number(y2)} ");
            }
        }
        return output.ToString().Trim();
    }
    public static string Band(IReadOnlyList<double?> lower, IReadOnlyList<double?> upper, double width, double height, double max)
    {
        if (lower.Count != upper.Count) throw new ArgumentException("Range arrays must align.");
        var mask = lower.Select((v, i) => v is { } low && upper[i] is { } high && double.IsFinite(low) && double.IsFinite(high) && low <= high ? v : null).ToArray();
        var output = new StringBuilder();
        foreach (var (start, end) in Segments(mask))
        {
            if(start==end)
            {
                var x=X(start,mask.Length,width);var half=Math.Min(8,width/Math.Max(1,mask.Length)/4);
                output.Append($"M {Number(Math.Max(0,x-half))} {Number(Y(upper[start]!.Value,0,max,height))} L {Number(Math.Min(width,x+half))} {Number(Y(upper[start]!.Value,0,max,height))} L {Number(Math.Min(width,x+half))} {Number(Y(lower[start]!.Value,0,max,height))} L {Number(Math.Max(0,x-half))} {Number(Y(lower[start]!.Value,0,max,height))} Z ");
                continue;
            }
            output.Append($"M {Number(X(start, mask.Length, width))} {Number(Y(upper[start]!.Value, 0, max, height))} ");
            for (var i = start + 1; i <= end; i++) output.Append($"L {Number(X(i, mask.Length, width))} {Number(Y(upper[i]!.Value, 0, max, height))} ");
            for (var i = end; i >= start; i--) output.Append($"L {Number(X(i, mask.Length, width))} {Number(Y(lower[i]!.Value, 0, max, height))} ");
            output.Append("Z ");
        }
        return output.ToString().Trim();
    }
    public static int Select(double x, double width, int count) => count <= 0 ? -1 : Math.Clamp((int)Math.Round(Math.Clamp(x, 0, width) / Math.Max(1, width) * (count - 1)), 0, count - 1);
}
