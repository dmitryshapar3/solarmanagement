using DeyeSolar.Web.Components.Ui;

namespace DeyeSolar.Web.Tests;

public class RoofSceneGeometryTests
{
    private const double Radians = Math.PI / 180;
    private const double Tolerance = 1e-7;

    [Theory]
    [InlineData(25, 230, 40, 50)]
    [InlineData(15, 90, 65, 270)]
    [InlineData(35, 17, 52, 132)]
    [InlineData(25, 180, 60, 180)]
    [InlineData(89.9, 230, 0, 50)]
    public void BothRoofPlanesAndTheirPanelsKeepTheirActualPitchAndBearing(double tilt1, double azimuth1, double tilt2, double azimuth2)
    {
        var roofs = RoofSunGeometry.Roofs(4, tilt1, azimuth1, 3, tilt2, azimuth2);
        var scene = RoofSceneGeometry.Create(roofs, null, 50, 20, 8, 7);

        foreach (var roof in roofs)
        {
            var surface = Assert.Single(scene.Faces.Where(f => f.Kind == "roof" && f.Roof == roof.Number));
            AssertPlane(surface.Points, roof.Tilt, roof.Azimuth);
            var panels = scene.Faces.Where(f => f.Kind == "panel" && f.Roof == roof.Number).ToArray();
            Assert.NotEmpty(panels);
            Assert.All(panels, panel => AssertPlane(panel.Points, roof.Tilt, roof.Azimuth));
        }
        AssertFinite(scene);
        if (tilt1 == 25 && azimuth1 == 230 && tilt2 == 40 && azimuth2 == 50 &&
            Environment.GetEnvironmentVariable("SOLAR_ROOF_QA_DIRECTORY") is { Length: > 0 } directory)
        {
            var at = DateTimeOffset.Parse("2026-10-10T10:00:00Z");
            var preview = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4.32, 25, 230, 3.24, 25, 50),
                RoofSunGeometry.Day(50.095278, 20.070278, "Europe/Warsaw", at), 50.095278, 20.070278, 8, 7, 4, 4);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "scene.json"), System.Text.Json.JsonSerializer.Serialize(preview,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        }
    }

    [Theory]
    [InlineData(25, 230, 25, 50)] // Opposite bearings.
    [InlineData(25, 230, 40, 50)] // Unequal slopes still meet at one ridge.
    [InlineData(35, 17, 52, 132)] // Arbitrary bearings.
    [InlineData(25, 180, 60, 180)] // Same bearing, different slopes.
    [InlineData(25, 180, 25, 180)] // Coincident planes share a seam.
    [InlineData(0, 0, 0, 270)]
    [InlineData(89.9, 270, 89.9, 90)]
    public void TwoRoofsShareRidgeVerticesAndEveryPerimeterEdgeMeetsAWall(double tilt1, double azimuth1, double tilt2, double azimuth2)
    {
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, tilt1, azimuth1, 3, tilt2, azimuth2), null, 50, 20);
        var roofs = scene.Faces.Where(f => f.Kind == "roof").OrderBy(f => f.Roof).ToArray();
        var walls = scene.Faces.Where(f => f.Kind == "wall").ToArray();
        Assert.Equal(2, roofs.Length);
        Assert.Equal(4, walls.Length);
        var common = roofs[0].Points.Where(p => roofs[1].Points.Any(q => Same(p, q))).ToArray();
        Assert.Equal(2, common.Length);
        Assert.True(Distance(common[0], common[1]) > Tolerance);

        var footprint = walls.Select(w => w.Points[0]).ToArray();
        // The two polygons partition the building footprint rather than leaving a hole or overlapping.
        Close(roofs.Sum(f => Area(f.Points)), Area(footprint));
        foreach (var roof in roofs)
        foreach (var (a, b) in Edges(roof.Points))
        {
            var perimeter = Edges(footprint).Any(edge => OnGroundSegment(a, edge.A, edge.B) && OnGroundSegment(b, edge.A, edge.B));
            if (perimeter)
                Assert.True(walls.Any(w => Edges(w.Points).Any(edge =>
                    (Same(a, edge.A) && Same(b, edge.B)) || (Same(a, edge.B) && Same(b, edge.A)))),
                    "A roof boundary must share its exact 3D edge with a wall.");
        }
        Assert.All(walls, wall =>
        {
            Assert.Equal(2, wall.Points.Count(p => Near(p[2], 0)));
            Assert.All(wall.Points, p => Assert.True(p[2] >= 0));
        });
        AssertFinite(scene);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EitherRoofCanBeShownAloneWithoutRenumbering(int number)
    {
        var roofs = RoofSunGeometry.Roofs(number == 1 ? 4 : 0, 30, 230, number == 2 ? 3 : 0, 45, 50);
        var scene = RoofSceneGeometry.Create(roofs, null, 50, 20, 8, 7);
        var roof = Assert.Single(scene.Faces.Where(f => f.Kind == "roof"));
        Assert.Equal(number, roof.Roof);
        AssertPlane(roof.Points, number == 1 ? 30 : 45, number == 1 ? 230 : 50);
        Assert.Equal(4, scene.Faces.Count(f => f.Kind == "wall"));
        Assert.Equal(number.ToString(), Assert.Single(scene.Labels.Where(l => l.Kind == "roof")).Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(89.9)]
    public void UniformFitPreservesTheSlopeAndFootprintAspectRatio(double tilt)
    {
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, tilt, 230, 0, 0, 0), null, 50, 20);
        var roof = Assert.Single(scene.Faces.Where(f => f.Kind == "roof"));
        var width = GroundDistance(roof.Points[0], roof.Points[1]);
        var depth = GroundDistance(roof.Points[1], roof.Points[2]);
        var rise = roof.Points.Max(p => p[2]) - roof.Points.Min(p => p[2]);
        Close(width / depth, .8);
        Close(rise / width, Math.Tan(tilt * Radians));
        Assert.InRange(scene.Faces.SelectMany(f => f.Points).Max(p => p[2]), 0, 176);
        AssertDefaultViewBounds(scene);
        AssertFinite(scene);
    }

    [Fact]
    public void TypicalRoofUsesTheLargerExtentWhileSunCompassAndDefaultCameraStayUnchanged()
    {
        var at = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var sun = new SolarDiagramPoint(0, 0, 90, 0, at);
        var day = new SolarDiagramDay(new(2026, 10, 10), "UTC", at, at.AddDays(1), [new[] { sun }], sun, null, null, "normal");
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4.32, 25, 230, 3.24, 25, 50), day, 50, 20, 8, 7, 4, 4);
        var footprint = scene.Faces.Where(f => f.Kind == "wall").Select(f => f.Points[0]).ToArray();
        Close(footprint.Max(a => footprint.Max(b => GroundDistance(a, b))), 176);
        Assert.Equal(8, scene.Faces.Count(f => f.Kind == "panel" && f.Roof == 1));
        Assert.Equal(7, scene.Faces.Count(f => f.Kind == "panel" && f.Roof == 2));
        Assert.All(scene.Ground, p => Close(Math.Sqrt(p[0] * p[0] + p[1] * p[1]), 110));
        Assert.All(scene.Labels.Where(l => l.Kind == "cardinal"), l => Close(Distance(l.Point, [0, 0, 0]), 126));
        Close(Distance(Assert.IsType<double[]>(scene.SunNow), [0, 0, 0]), 110);
        var origin = RoofSceneGeometry.Project([0, 0, 0]);
        Close(origin.X, 180); Close(origin.Y, 180);
        AssertDefaultViewBounds(scene);
    }

    [Fact]
    public void DefaultPitchFitsWholeFacetsPanelsSupportsAndBadgeClearanceAcrossCompassBearingsSlopesAndCameraYaw()
    {
        // Includes the 65° west-facing / flat-roof combination which clips above the
        // stage when the 176-unit extent is used without a projected uniform fit.
        double[] tilts = [0, 20, 25, 45, 65, 89, 90];
        foreach (var tilt1 in tilts)
        foreach (var tilt2 in tilts)
        for (var azimuth = 0; azimuth < 360; azimuth += 15)
        foreach (var offset in new[] { 0, 45, 90, 180 })
        {
            var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, tilt1, azimuth, 3, tilt2, (azimuth + offset) % 360), null, 50, 20, 8, 7, 4, 4);
            foreach (var yaw in new[] { -180d, -135, -90, -35, 0, 45, 90, 135 }) AssertDefaultViewBounds(scene, yaw);
            Assert.Equal(8, scene.Faces.Count(f => f.Kind == "panel" && f.Roof == 1));
            Assert.Equal(7, scene.Faces.Count(f => f.Kind == "panel" && f.Roof == 2));
        }
    }

    [Theory]
    [InlineData(25, 230, 25, 50)]
    [InlineData(65, 270, 0, 270)]
    [InlineData(89, 137, 25, 13)]
    [InlineData(90, 110, 25, 300)]
    [InlineData(90, 270, 90, 90)]
    public void TurningBothRoofBearingsKeepsTheBuildingSizePitchAndPanelAspectRatio(double tilt1, double azimuth1, double tilt2, double azimuth2)
    {
        var reference = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, tilt1, azimuth1, 3, tilt2, azimuth2), null, 50, 20, 8, 7, 4, 4);
        double Diameter(RoofScene scene)
        {
            var footprint = scene.Faces.Where(f => f.Kind == "wall").Select(f => f.Points[0]).ToArray();
            return footprint.Max(a => footprint.Max(b => GroundDistance(a, b)));
        }
        var diameter = Diameter(reference);
        var height = reference.Faces.SelectMany(f => f.Points).Max(p => p[2]);
        for (var angle = 15; angle < 360; angle += 15)
        {
            var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, tilt1, (azimuth1 + angle) % 360, 3, tilt2, (azimuth2 + angle) % 360), null, 50, 20, 8, 7, 4, 4);
            Close(Diameter(scene), diameter);
            Close(scene.Faces.SelectMany(f => f.Points).Max(p => p[2]), height);
            foreach (var number in new[] { 1, 2 })
            {
                var surface = Assert.Single(scene.Faces.Where(f => f.Roof == number && f.Kind is "roof" or "vertical"));
                AssertPlane(surface.Points, number == 1 ? tilt1 : tilt2, ((number == 1 ? azimuth1 : azimuth2) + angle) % 360);
                Assert.All(scene.Faces.Where(f => f.Kind == "panel" && f.Roof == number), panel =>
                    Close(Distance(panel.Points[1], panel.Points[2]) / Distance(panel.Points[0], panel.Points[1]), 1.7));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(230)]
    public void ExactlyVerticalInputIsAUprightArrayWithItsActualBearing(double azimuth)
    {
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, 90, azimuth, 0, 0, 0), null, 50, 20);
        var panel = Assert.Single(scene.Faces.Where(f => f.Kind == "vertical"));
        var normal = Normal(panel.Points);
        Close(normal[2], 0);
        // A vertical plane is parallel to the vertical axis and perpendicular to its stated bearing.
        Close(Math.Abs(normal[0] * Math.Sin(azimuth * Radians) + normal[1] * Math.Cos(azimuth * Radians)), 1);
        Assert.Equal(1, panel.Roof);
        Assert.Equal(4, scene.Faces.Count(f => f.Kind == "wall"));
        AssertFinite(scene);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(90)]
    [InlineData(230)]
    [InlineData(270)]
    public void OppositeVerticalArraysStayOnSeparateSidesOfTheSharedBuilding(double azimuth)
    {
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, 90, azimuth, 3, 90, (azimuth + 180) % 360), null, 50, 20, 8, 7);
        var first = Assert.Single(scene.Faces.Where(f => f.Kind == "vertical" && f.Roof == 1));
        var second = Assert.Single(scene.Faces.Where(f => f.Kind == "vertical" && f.Roof == 2));
        var firstCenter = Enumerable.Range(0, 3).Select(i => first.Points.Average(p => p[i])).ToArray();
        var secondCenter = Enumerable.Range(0, 3).Select(i => second.Points.Average(p => p[i])).ToArray();
        var bearing = new[] { Math.Sin(azimuth * Radians), Math.Cos(azimuth * Radians) };
        Assert.True(firstCenter[0] * bearing[0] + firstCenter[1] * bearing[1] < 0);
        Assert.True(secondCenter[0] * bearing[0] + secondCenter[1] * bearing[1] > 0);
        var normal = Normal(first.Points);
        Assert.True(Math.Abs(normal.Select((value, i) => value * (secondCenter[i] - firstCenter[i])).Sum()) > 1,
            "Opposite upright arrays must occupy separate parallel planes, rather than coinciding.");
        AssertPlane(first.Points, 90, azimuth);
        AssertPlane(second.Points, 90, (azimuth + 180) % 360);
        AssertDefaultViewBounds(scene);
    }

    [Fact]
    public void VerticalArrayClearsTheInclinedRoofAtBothEndsAndItsSupportsMeetTheRoof()
    {
        // An east/west upright array crosses the slope of a southwest-facing roof.
        // Placing it at the roof's centre height would bury one end inside the roof.
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, 25, 230, 3, 90, 0), null, 50, 20);
        var roof = Assert.Single(scene.Faces.Where(f => f.Kind == "roof"));
        var panel = Assert.Single(scene.Faces.Where(f => f.Kind == "vertical" && f.Roof == 2));
        Close(Normal(panel.Points)[2], 0);
        var normal = Normal(roof.Points);
        var origin = roof.Points[0];
        double RoofHeightAt(double[] p) => origin[2] -
            (normal[0] * (p[0] - origin[0]) + normal[1] * (p[1] - origin[1])) / normal[2];
        var bottomHeight = panel.Points.Min(p => p[2]);
        var bottomEnds = panel.Points.Where(p => Near(p[2], bottomHeight)).ToArray();
        Assert.Equal(2, bottomEnds.Length);
        var supports = scene.Lines.Where(l => l.Kind == "bearing").ToArray();
        Assert.Equal(2, supports.Length);
        foreach (var end in bottomEnds)
        {
            Assert.True(end[2] > RoofHeightAt(end));
            var support = Assert.Single(supports.Where(l => l.Points.Any(p => Same(p, end))));
            var foot = Assert.Single(support.Points.Where(p => !Same(p, end)));
            Close(foot[0], end[0]); Close(foot[1], end[1]);
            Close(foot[2], RoofHeightAt(foot));
            Assert.True(foot[2] < end[2]);
        }
        AssertFinite(scene);
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(90, "E")]
    [InlineData(180, "S")]
    [InlineData(270, "W")]
    public void HorizonSunAndCompassKeepTheSameBearingThroughOrbitAndZoom(double azimuth, string cardinal)
    {
        var at = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var sun = new SolarDiagramPoint(0, 0, azimuth, 0, at);
        var day = new SolarDiagramDay(new(2026, 10, 10), "UTC", at, at.AddDays(1), [new[] { sun }], sun, null, null, "normal");
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, 25, 230, 3, 25, 50), day, 50, 20);
        var label = Assert.Single(scene.Labels.Where(l => l.Text == cardinal));
        var current = Assert.IsType<double[]>(scene.SunNow);
        Assert.True(Same(current, Assert.Single(Assert.Single(scene.SunPaths))));

        foreach (var (yaw, elevation, zoom) in new[] { (-35d, 32d, 1d), (80d, 15d, .7d), (210d, 70d, 1.6d) })
        {
            var origin = RoofSceneGeometry.Project([0, 0, 0], yaw, elevation, zoom);
            var projectedSun = RoofSceneGeometry.Project(current, yaw, elevation, zoom);
            var projectedCardinal = RoofSceneGeometry.Project(label.Point, yaw, elevation, zoom);
            Close(projectedSun.X - origin.X, (projectedCardinal.X - origin.X) * 110 / 126);
            Close(projectedSun.Y - origin.Y, (projectedCardinal.Y - origin.Y) * 110 / 126);
            Close(projectedSun.Depth, projectedCardinal.Depth * 110 / 126);
        }
    }

    [Theory]
    [InlineData(double.NaN, 25, 230)]
    [InlineData(double.PositiveInfinity, 25, 230)]
    [InlineData(-1, 25, 230)]
    [InlineData(10001, 25, 230)]
    [InlineData(4, double.NaN, 230)]
    [InlineData(4, -1, 230)]
    [InlineData(4, 91, 230)]
    [InlineData(4, 25, double.PositiveInfinity)]
    [InlineData(4, 25, 361)]
    public void InvalidDraftRoofIsOmittedWithoutBreakingTheRemainingScene(double capacity, double tilt, double azimuth)
    {
        var roofs = RoofSunGeometry.Roofs(capacity, tilt, azimuth, 3, 40, 50);
        var scene = RoofSceneGeometry.Create(roofs, null, 50, 20, 8, 7);
        Assert.DoesNotContain(scene.Faces, face => face.Roof == 1);
        Assert.Equal(2, Assert.Single(scene.Faces.Where(f => f.Kind == "roof")).Roof);
        AssertFinite(scene);
        var empty = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(capacity, tilt, azimuth, 0, 0, 0), null, 50, 20);
        Assert.Empty(empty.Faces);
        Assert.Null(empty.SunNow);
        Assert.Empty(empty.SunPaths);
        AssertFinite(empty);
    }

    [Theory]
    [InlineData(1, 1, 25, 230)]
    [InlineData(7, 4, 25, 230)]
    [InlineData(8, 4, 0, 50)]
    [InlineData(17, 5, 35, 17)]
    [InlineData(17, 0, 89.9, 230)]
    [InlineData(7, 3, 90, 90)]
    public void EverySpecifiedPanelIsAWholeRectangleInsideItsOwnFacet(int count, int columns, double tilt, double azimuth)
    {
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, tilt, azimuth, 3, 52, 132), null, 50, 20, count, 8, columns, 4);
        var surface = Assert.Single(scene.Faces.Where(f => f.Roof == 1 && f.Kind is "roof" or "vertical"));
        var panels = scene.Faces.Where(f => f.Kind == "panel" && f.Roof == 1).ToArray();
        Assert.Equal(count, panels.Length);
        Assert.Equal(8, scene.Faces.Count(f => f.Kind == "panel" && f.Roof == 2));
        var normal = Normal(surface.Points);
        var origin = surface.Points[0];
        double Dot(double[] p) => normal.Select((n, i) => n * (p[i] - origin[i])).Sum();
        var offset = Dot(panels[0].Points[0]);
        Assert.True(offset > 0);
        foreach (var panel in panels)
        {
            Assert.Equal(4, panel.Points.Length);
            AssertPlane(panel.Points, tilt, azimuth);
            Close(Distance(panel.Points[1], panel.Points[2]) / Distance(panel.Points[0], panel.Points[1]), 1.7);
            foreach (var point in panel.Points)
            {
                Close(Dot(point), offset);
                var onSurface = point.Select((value, i) => value - normal[i] * offset).ToArray();
                foreach (var (a, b) in Edges(surface.Points))
                {
                    var edge = b.Select((value, i) => value - a[i]).ToArray();
                    var delta = onSurface.Select((value, i) => value - a[i]).ToArray();
                    var cross = new[] { edge[1] * delta[2] - edge[2] * delta[1], edge[2] * delta[0] - edge[0] * delta[2], edge[0] * delta[1] - edge[1] * delta[0] };
                    Assert.True(cross.Select((value, i) => value * normal[i]).Sum() >= -Tolerance, "A complete panel must stay inside its roof facet.");
                }
            }
        }
        var u = new[] { Math.Cos(azimuth * Radians), -Math.Sin(azimuth * Radians), 0 };
        var v = new[] { Math.Sin(azimuth * Radians) * Math.Cos(tilt * Radians), Math.Cos(azimuth * Radians) * Math.Cos(tilt * Radians), -Math.Sin(tilt * Radians) };
        double Coord(double[] p, double[] basis) => p.Select((value, i) => value * basis[i]).Sum();
        var rectangles = panels.Select(p => (U: p.Points.Select(q => Coord(q, u)).ToArray(), V: p.Points.Select(q => Coord(q, v)).ToArray())).ToArray();
        for (var i = 0; i < rectangles.Length; ++i)
        for (var j = i + 1; j < rectangles.Length; ++j)
        {
            var a = rectangles[i]; var b = rectangles[j];
            Assert.True(a.U.Max() < b.U.Min() || b.U.Max() < a.U.Min() || a.V.Max() < b.V.Min() || b.V.Max() < a.V.Min(), "Panel interiors must not overlap.");
        }
        if (columns > 0)
        {
            var rows = rectangles.GroupBy(p => Math.Round(p.V.Average(), 6)).OrderBy(g => g.Key).ToArray();
            Assert.Equal((count + columns - 1) / columns, rows.Length);
            Assert.Equal(count - columns * (rows.Length - 1), rows[^1].Count());
            Assert.All(rows.Take(rows.Length - 1), row => Assert.Equal(columns, row.Count()));
            Assert.All(rows, row => Close(row.Average(p => p.U.Average()), rows[0].Average(p => p.U.Average())));
        }
        AssertFinite(scene);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void UnknownZeroAndInvalidPanelCountsNeverInventPanelsFromCapacity(int? count)
    {
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4.32, 25, 230, 3.78, 90, 50), null, 50, 20, count, count);
        Assert.DoesNotContain(scene.Faces, f => f.Kind == "panel");
        Assert.Single(scene.Faces.Where(f => f.Kind == "roof"));
        Assert.Single(scene.Faces.Where(f => f.Kind == "vertical"));
        AssertFinite(scene);
    }

    [Fact]
    public void MaximumValidCountIsExactAndPerRoofRowChoicesAreIndependent()
    {
        var scene = RoofSceneGeometry.Create(RoofSunGeometry.Roofs(4, 25, 230, 3, 25, 50), null, 50, 20, 1000, 7, 100, 3);
        Assert.Equal(1000, scene.Faces.Count(f => f.Kind == "panel" && f.Roof == 1));
        Assert.Equal(7, scene.Faces.Count(f => f.Kind == "panel" && f.Roof == 2));
        AssertFinite(scene);
    }

    private static void AssertPlane(double[][] polygon, double tilt, double azimuth)
    {
        var normal = Normal(polygon);
        if (normal[2] < 0) normal = normal.Select(v => -v).ToArray();
        Close(normal[0], Math.Sin(tilt * Radians) * Math.Sin(azimuth * Radians));
        Close(normal[1], Math.Sin(tilt * Radians) * Math.Cos(azimuth * Radians));
        Close(normal[2], Math.Cos(tilt * Radians));
        var origin = polygon[0];
        Assert.All(polygon, point => Close(normal.Select((value, i) => value * (point[i] - origin[i])).Sum(), 0));
    }

    private static double[] Normal(double[][] polygon)
    {
        for (var i = 1; i < polygon.Length - 1; ++i)
        {
            var a = polygon[i].Select((value, j) => value - polygon[0][j]).ToArray();
            var b = polygon[i + 1].Select((value, j) => value - polygon[0][j]).ToArray();
            var cross = new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
            var length = Math.Sqrt(cross.Sum(v => v * v));
            if (length > 1e-12) return cross.Select(v => v / length).ToArray();
        }
        throw new InvalidOperationException("A rendered polygon must have positive area.");
    }

    private static IEnumerable<(double[] A, double[] B)> Edges(double[][] points)
        => points.Select((point, index) => (point, points[(index + 1) % points.Length]));
    private static double Area(double[][] points)
        => Math.Abs(Edges(points).Sum(edge => edge.A[0] * edge.B[1] - edge.B[0] * edge.A[1])) / 2;
    private static double GroundDistance(double[] a, double[] b) => Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2));
    private static bool OnGroundSegment(double[] point, double[] a, double[] b)
        => Near(GroundDistance(a, point) + GroundDistance(point, b), GroundDistance(a, b));
    private static double Distance(double[] a, double[] b) => Math.Sqrt(a.Select((value, i) => Math.Pow(value - b[i], 2)).Sum());
    private static bool Same(double[] a, double[] b) => Distance(a, b) < Tolerance;
    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < Tolerance;
    private static void Close(double actual, double expected) => Assert.True(Near(actual, expected), $"Expected {expected:R}, actual {actual:R}");
    private static void AssertDefaultViewBounds(RoofScene scene, double yaw = -35)
    {
        var points = scene.Faces.SelectMany(f => f.Points).Concat(scene.Lines.SelectMany(l => l.Points));
        Assert.All(points, point =>
        {
            var projected = RoofSceneGeometry.Project(point, yaw);
            Assert.InRange(projected.X, 12 - Tolerance, 348 + Tolerance);
            Assert.InRange(projected.Y, 12 - Tolerance, 300 + Tolerance);
        });
    }
    private static void AssertFinite(RoofScene scene)
    {
        var points = scene.Faces.SelectMany(f => f.Points).Concat(scene.Lines.SelectMany(l => l.Points))
            .Concat(scene.Labels.Select(l => l.Point)).Concat(scene.SunPaths.SelectMany(p => p))
            .Concat(scene.Crossings).Concat(scene.Ground).Concat(scene.Axes.SelectMany(a => a));
        if (scene.SunNow is { } current) points = points.Append(current);
        Assert.All(points, point =>
        {
            Assert.Equal(3, point.Length);
            Assert.All(point, value => Assert.True(double.IsFinite(value)));
            var projected = RoofSceneGeometry.Project(point);
            Assert.True(double.IsFinite(projected.X) && double.IsFinite(projected.Y) && double.IsFinite(projected.Depth));
        });
    }
}
