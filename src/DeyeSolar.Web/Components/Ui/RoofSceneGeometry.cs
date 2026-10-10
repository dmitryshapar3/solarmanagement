namespace DeyeSolar.Web.Components.Ui;

public sealed record RoofSceneFace(string Kind, int Roof, double[][] Points);
public sealed record RoofSceneLine(string Kind, double[][] Points);
public sealed record RoofSceneLabel(string Text, string Kind, double[] Point);
public sealed record RoofScene(IReadOnlyList<RoofSceneFace> Faces, IReadOnlyList<RoofSceneLine> Lines,
    IReadOnlyList<RoofSceneLabel> Labels, double[][][] SunPaths, double[]? SunNow, double[][] Crossings,
    double[][] Ground, double[][][] Axes);
public sealed record RoofSceneProjection(double X, double Y, double Depth);

/// <summary>Connected schematic roof in east/north/up coordinates. Geometry preserves each plane's pitch and bearing.</summary>
public static class RoofSceneGeometry
{
    private const double Rad = Math.PI / 180;
    private static readonly double[][] LocalFootprint = [[-32, -40], [32, -40], [32, 40], [-32, 40]];
    private sealed record Plane(SolarDiagramRoof Roof, double X, double Y);

    public static RoofScene Create(IReadOnlyList<SolarDiagramRoof> roofs, SolarDiagramDay? day,
        double latitude, double longitude)
    {
        var planes = roofs.Where(r => r.Tilt < 90).Select(r => new Plane(r,
            Math.Tan(r.Tilt * Rad) * Math.Sin(r.Azimuth * Rad), Math.Tan(r.Tilt * Rad) * Math.Cos(r.Azimuth * Rad))).ToArray();
        var bearing = (roofs.FirstOrDefault(r => r.Tilt < 90) ?? roofs.FirstOrDefault())?.Azimuth * Rad ?? 0;
        double[] World(double x, double y) => [x * Math.Sin(bearing) - y * Math.Cos(bearing), x * Math.Cos(bearing) + y * Math.Sin(bearing)];
        var footprint = LocalFootprint.Select(p => World(p[0], p[1])).ToArray();
        var ridgeHeight = 14 + planes.SelectMany(p => footprint.Select(v => Math.Abs(p.X * v[0] + p.Y * v[1]))).DefaultIfEmpty(0).Max();
        double Height(double[] point) => planes.Length == 0 ? 14 : planes.Min(p => ridgeHeight - p.X * point[0] - p.Y * point[1]);
        var faces = new List<RoofSceneFace>(); var lines = new List<RoofSceneLine>(); var labels = new List<RoofSceneLabel>();
        double[] OnPlane(Plane p, double[] v, double lift = 0) => [v[0], v[1], ridgeHeight - p.X * v[0] - p.Y * v[1] + lift];
        var division = planes.Length == 2 ? new[] { planes[0].X - planes[1].X, planes[0].Y - planes[1].Y } : new[] { 0d, 0d };
        if (planes.Length == 2 && Math.Abs(division[0]) + Math.Abs(division[1]) < 1e-8)
            division = [Math.Sin(planes[0].Roof.Azimuth * Rad), Math.Cos(planes[0].Roof.Azimuth * Rad)];
        for (var index = 0; index < planes.Length; ++index)
        {
            var plane = planes[index]; var side = index == 0 ? 1 : -1;
            var polygon = planes.Length == 2 ? Clip(footprint, p => side * (division[0] * p[0] + division[1] * p[1])) : footprint;
            if (polygon.Length < 3) continue;
            faces.Add(new("roof", plane.Roof.Number, polygon.Select(p => OnPlane(plane, p)).ToArray()));
            var center = new[] { polygon.Average(p => p[0]), polygon.Average(p => p[1]) };
            labels.Add(new(plane.Roof.Number.ToString(), "roof", OnPlane(plane, center, 2)));
            // A regular PV lattice lies on the actual roof plane; edge tiles are clipped to its footprint.
            var coverage = .68 + .14 * Math.Sqrt(plane.Roof.Capacity / roofs.Max(r => r.Capacity));
            var inset = polygon.Select(p => new[] { center[0] + (p[0] - center[0]) * coverage, center[1] + (p[1] - center[1]) * coverage }).ToArray();
            for (double x = -28; x < 28; x += 14)
                for (double y = -36; y < 36; y += 12)
                {
                    double[][] tile = [World(x, y), World(x + 12.5, y), World(x + 12.5, y + 10.5), World(x, y + 10.5)];
                    for (var edge = 0; edge < inset.Length && tile.Length >= 3; ++edge)
                    {
                        var a = inset[edge]; var b = inset[(edge + 1) % inset.Length];
                        tile = Clip(tile, p => (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]));
                    }
                    if (tile.Length >= 3 && Area(tile) > 4) faces.Add(new("panel", plane.Roof.Number, tile.Select(p => OnPlane(plane, p, .3)).ToArray()));
                }
        }
        if (planes.Length == 0 && roofs.Count > 0)
            faces.Add(new("roof", 0, footprint.Select(p => new[] { p[0], p[1], 14d }).ToArray()));
        if (roofs.Count > 0)
            for (var edge = 0; edge < footprint.Length; ++edge)
            {
                var a = footprint[edge]; var b = footprint[(edge + 1) % footprint.Length];
                List<double[]> vertices = [[a[0], a[1], 0], [b[0], b[1], 0], [b[0], b[1], Height(b)]];
                var da = division[0] * a[0] + division[1] * a[1]; var db = division[0] * b[0] + division[1] * b[1];
                if (planes.Length == 2 && da * db < 0)
                {
                    var ratio = da / (da - db); var cut = new[] { a[0] + ratio * (b[0] - a[0]), a[1] + ratio * (b[1] - a[1]) };
                    vertices.Add([cut[0], cut[1], Height(cut)]);
                }
                vertices.Add([a[0], a[1], Height(a)]); faces.Add(new("wall", 0, vertices.ToArray()));
            }
        // A 90° input is represented truthfully as an upright PV array on the shared building,
        // rather than silently replacing its pitch with a nearly vertical height field.
        foreach (var roof in roofs.Where(r => r.Tilt == 90))
        {
            var a = roof.Azimuth * Rad; var dx = -Math.Cos(a); var dy = Math.Sin(a);
            var offset = roofs.Count(r => r.Tilt == 90) == 1 ? 0 : roof.Number == 1 ? -10 : 10;
            var center = new[] { Math.Sin(a) * offset, Math.Cos(a) * offset };
            var left = new[] { center[0] - dx * 24, center[1] - dy * 24 }; var right = new[] { center[0] + dx * 24, center[1] + dy * 24 };
            var bottom = Math.Max(Height(left), Height(right)) + .5;
            foreach(var foot in new[]{left,right}) lines.Add(new("bearing", [[foot[0],foot[1],Height(foot)],[foot[0],foot[1],bottom]]));
            double[][] polygon = [[center[0] - dx * 24, center[1] - dy * 24, bottom], [center[0] + dx * 24, center[1] + dy * 24, bottom],
                [center[0] + dx * 24, center[1] + dy * 24, bottom + 26], [center[0] - dx * 24, center[1] - dy * 24, bottom + 26]];
            faces.Add(new("panel", roof.Number, polygon)); labels.Add(new(roof.Number.ToString(), "roof", [center[0], center[1], bottom + 14]));
            for (var i = 1; i < 4; ++i)
            {
                var t = i / 4d; var x = polygon[0][0] + (polygon[1][0] - polygon[0][0]) * t; var y = polygon[0][1] + (polygon[1][1] - polygon[0][1]) * t;
                lines.Add(new("grid", [[x, y, bottom], [x, y, bottom + 26]]));
            }
        }
        var maxHeight = faces.SelectMany(f => f.Points).Select(p => p[2]).DefaultIfEmpty(0).Max();
        var width = footprint.Max(p => p[0]) - footprint.Min(p => p[0]);
        var depth = footprint.Max(p => p[1]) - footprint.Min(p => p[1]);
        var scale = 112 / Math.Max(Math.Max(width, depth), maxHeight);
        double[] Fit(double[] p) => [p[0] * scale, p[1] * scale, p[2] * scale];
        faces = faces.Select(f => f with { Points = f.Points.Select(Fit).ToArray() }).ToList();
        lines = lines.Select(l => l with { Points = l.Points.Select(Fit).ToArray() }).ToList();
        labels = labels.Select(l => l with { Point = Fit(l.Point) }).ToList();
        labels.AddRange(new[] { new RoofSceneLabel("N", "cardinal", [0, 126, 0]), new("E", "cardinal", [126, 0, 0]),
            new("S", "cardinal", [0, -126, 0]), new("W", "cardinal", [-126, 0, 0]) });
        double[] Sun(SolarDiagramPoint p) => [110 * Math.Cos(p.Elevation * Rad) * Math.Sin(p.Azimuth * Rad),
            110 * Math.Cos(p.Elevation * Rad) * Math.Cos(p.Azimuth * Rad), 110 * Math.Sin(Math.Max(0, p.Elevation) * Rad)];
        var crossings = day is null ? [] : new[] { day.Sunrise, day.Sunset }.Where(p => p.HasValue).Select(p => Sun(RoofSunGeometry.Position(latitude, longitude, p!.Value))).ToArray();
        return new(faces, lines, labels, day?.Paths.Select(p => p.Select(Sun).ToArray()).ToArray() ?? [], day?.Now is { } now ? Sun(now) : null, crossings,
            Enumerable.Range(0, 65).Select(i => new[] { 110 * Math.Sin(i * 2 * Math.PI / 64), 110 * Math.Cos(i * 2 * Math.PI / 64), 0d }).ToArray(),
            [[[-110, 0, 0], [110, 0, 0]], [[0, -110, 0], [0, 110, 0]]]);
    }

    public static RoofSceneProjection Project(double[] p, double yaw = -35, double elevation = 32, double zoom = 1)
    {
        var a = yaw * Rad; var e = elevation * Rad; var depth = p[0] * Math.Sin(a) + p[1] * Math.Cos(a);
        return new(180 + (p[0] * Math.Cos(a) - p[1] * Math.Sin(a)) * zoom,
            180 + (depth * Math.Sin(e) - p[2] * Math.Cos(e)) * zoom, depth * Math.Cos(e) + p[2] * Math.Sin(e));
    }
    public static string Points(IEnumerable<double[]> points) => string.Join(" ", points.Select(p => { var q = Project(p); return $"{RoofSunGeometry.F(q.X)},{RoofSunGeometry.F(q.Y)}"; }));
    public static string Path(IEnumerable<double[]> points) => string.Join(" ", points.Select((p, i) => { var q = Project(p); return $"{(i == 0 ? "M" : "L")}{RoofSunGeometry.F(q.X)} {RoofSunGeometry.F(q.Y)}"; }));
    private static double Area(double[][] polygon) => Math.Abs(polygon.Select((p, i) => p[0] * polygon[(i + 1) % polygon.Length][1] - p[1] * polygon[(i + 1) % polygon.Length][0]).Sum()) / 2;
    private static double[][] Clip(double[][] polygon, Func<double[], double> distance)
    {
        var output = new List<double[]>();
        for (var i = 0; i < polygon.Length; ++i)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length]; var da = distance(a); var db = distance(b);
            if (da >= -1e-8) output.Add(a);
            if ((da >= 0) != (db >= 0)) { var t = da / (da - db); output.Add([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t]); }
        }
        return output.ToArray();
    }
}
