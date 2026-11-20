using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.InnerSpace;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.InnerSpace;

public class InnerSpaceMaterialsTests
{
    private static InnerSpaceMaterials With(params InnerSpaceWallType[] types) => new(types);

    [Theory]
    [InlineData("Exterior Residential", "exterior_residential")]
    [InlineData("exterior (residential)", "exterior_residential")]
    [InlineData("EXTERIOR_COMMERCIAL", "exterior_commercial")]
    [InlineData("Drywall (Heavy Duty)", "drywall_heavy")]
    [InlineData("Stucco", null)]
    public void ByName_IgnoresCaseSpacesAndPunctuation(string name, string? expected) =>
        InnerSpaceMaterials.ByName(name).Should().Be(expected);

    [Fact]
    public void BuiltIns_MapByKey_OpeningsCarryDefaultSpans()
    {
        var m = With();
        m.Resolve("drywall", null, false).Should().Be(new InnerSpaceMaterials.Resolved("drywall", null, false));
        m.Resolve("window_1_pane", null, false).Span.Should().Be(InnerSpaceMaterials.WindowSpan);
        m.Resolve("door_metal", null, true).Span.Should().Be(InnerSpaceMaterials.DoorSpan);
    }

    [Fact]
    public void ScannedConcrete_IsTheScanDefault_UnlessOverridden()
    {
        With().Resolve("concrete", null, isScannedPlan: true).IsScanDefault.Should().BeTrue();
        With().Resolve("concrete", null, isScannedPlan: false).Material.Should().Be("concrete");

        var overridden = With(new InnerSpaceWallType { Id = "t1", Name = "Exterior Residential", Attenuation = 8, OverridesVariant = "concrete" });
        overridden.Resolve("concrete", null, isScannedPlan: true)
            .Should().Be(new InnerSpaceMaterials.Resolved("exterior_residential", null, false));
    }

    [Fact]
    public void OverrideWithUnknownName_KeepsTheBuiltIn()
    {
        var m = With(new InnerSpaceWallType { Id = "t1", Name = "Stucco", Attenuation = 6, OverridesVariant = "brick" });
        m.Resolve("brick", null, false).Material.Should().Be("brick");
    }

    [Fact]
    public void CustomType_MapsByName_ElseNearestLoss_AndKeepsItsHeights()
    {
        var m = With(
            new InnerSpaceWallType { Id = "named", Name = "Exterior Residential", Attenuation = 8, OverridesVariant = "concrete" },
            new InnerSpaceWallType { Id = "odd", Name = "Shipping Container", Attenuation = 9, BottomHeight = 0.5, TopHeight = 2.2 });

        m.Resolve("custom", "named", true).Material.Should().Be("exterior_residential");
        var odd = m.Resolve("custom", "odd", false);
        odd.Material.Should().Be("metal"); // 10 dB at 5 GHz is the closest to 9
        odd.Span.Should().Be(new WallSpan(0.5, 2.2));
    }

    [Fact]
    public void DeletedTypes_AreIgnored()
    {
        var m = With(new InnerSpaceWallType { Id = "t1", Name = "Exterior Residential", OverridesVariant = "concrete", IsDeleted = true });
        m.Resolve("concrete", null, false).Material.Should().Be("concrete");
    }
}

public class TemplatePlacerTests
{
    [Fact]
    public void ToLatLng_RotatesClockwise()
    {
        var p = new Placement(40, -90, 90);
        var (lat, lng) = TemplatePlacer.ToLatLng(new Vec2(0, 10), p); // 10 m up the plan

        (lat - 40).Should().BeApproximately(0, 1e-9);
        TemplatePlacer.ToLocal(lat, lng, 40, -90).X.Should().BeApproximately(10, 1e-6); // now 10 m east
    }

    [Fact]
    public void WallsJson_WritesPerSegmentMaterialsAndSpans_InTheEditorsShape()
    {
        var floor = new ImportFloor();
        var w = new ImportPolyline();
        w.Points.AddRange(new[] { new Vec2(0, 0), new Vec2(1, 0), new Vec2(2, 0) });
        w.Materials.AddRange(new[] { "drywall", "window_2_pane" });
        w.Spans.AddRange(new WallSpan?[] { null, new WallSpan(1, 2) });
        floor.Walls.Add(w);

        using var doc = JsonDocument.Parse(TemplatePlacer.WallsJson(floor, new Placement(10, 20, 0)));
        var wall = doc.RootElement[0];
        wall.GetProperty("points").GetArrayLength().Should().Be(3);
        wall.GetProperty("points")[0].GetProperty("lat").GetDouble().Should().Be(10);
        wall.GetProperty("material").GetString().Should().Be("drywall");
        wall.GetProperty("materials")[1].GetString().Should().Be("window_2_pane");
        wall.GetProperty("spans")[0].ValueKind.Should().Be(JsonValueKind.Null);
        wall.GetProperty("spans")[1].GetProperty("bottom").GetDouble().Should().Be(1);
    }

    [Fact]
    public void WallsJson_UniformWallWithoutSpans_StaysCompact()
    {
        var floor = new ImportFloor();
        var w = new ImportPolyline();
        w.Points.AddRange(new[] { new Vec2(0, 0), new Vec2(1, 0) });
        w.Materials.Add("brick");
        w.Spans.Add(null);
        floor.Walls.Add(w);

        var json = TemplatePlacer.WallsJson(floor, new Placement(10, 20, 0));
        json.Should().NotContain("materials").And.NotContain("spans");
    }

    [Fact]
    public void FitToOutline_RecoversRotationAndPosition()
    {
        // A 12 x 6 m footprint, laid down at a known spot and a 30 degree clockwise turn.
        var footprint = new List<Vec2> { new(-6, -3), new(6, -3), new(6, 3), new(-6, 3) };
        var truth = new Placement(35.0, -97.0, 30);
        var outline = footprint.Select(p => TemplatePlacer.ToLatLng(p, truth)).ToList();

        var fit = TemplatePlacer.FitToOutline(footprint, outline);

        fit.Lat.Should().BeApproximately(truth.Lat, 1e-7);
        fit.Lng.Should().BeApproximately(truth.Lng, 1e-7);
        // A rectangle matches itself turned half way round too.
        var turn = ((fit.RotationDeg - truth.RotationDeg) % 180 + 180) % 180;
        Math.Min(turn, 180 - turn).Should().BeLessThan(0.5);
    }

    [Fact]
    public void FitToOutline_ClusteredCornersFromWindowCuts_DoNotPullTheFit()
    {
        // Real outlines carry many extra corners where windows and doors are cut, all on one side.
        // Those must not move or tilt the fit (they pulled a real one 1.9 m off).
        var footprint = new List<Vec2> { new(-6, -3), new(6, -3), new(6, 3), new(-6, 3) };
        var cut = new List<Vec2> { new(-6, -3) };
        for (var x = -5.5; x < 6; x += 0.25) cut.Add(new Vec2(x, -3)); // dense corners on the south wall
        cut.AddRange(new[] { new Vec2(6, -3), new Vec2(6, 3), new Vec2(-6, 3) });
        var truth = new Placement(35.0, -97.0, 30);
        var outline = cut.Select(p => TemplatePlacer.ToLatLng(p, truth)).ToList();

        var fit = TemplatePlacer.FitToOutline(footprint, outline);

        TemplatePlacer.ToLocal(fit.Lat, fit.Lng, truth.Lat, truth.Lng).Length.Should().BeLessThan(0.01);
        var turn = ((fit.RotationDeg - truth.RotationDeg) % 180 + 180) % 180;
        Math.Min(turn, 180 - turn).Should().BeLessThan(0.1);
    }

    [Fact]
    public void FitCandidates_AreTheFourQuarterTurns_BestFirst()
    {
        // An L shape fits one way only, so the first candidate is exact and the rest are worse turns.
        var footprint = new List<Vec2> { new(0, 0), new(8, 0), new(8, 3), new(3, 3), new(3, 6), new(0, 6) };
        var truth = new Placement(35.0, -97.0, 200);
        var outline = footprint.Select(p => TemplatePlacer.ToLatLng(p, truth)).ToList();

        var c = TemplatePlacer.FitCandidates(footprint, outline);

        c.Should().HaveCount(4);
        Math.Abs(((c[0].RotationDeg - truth.RotationDeg) % 360 + 540) % 360 - 180).Should().BeLessThan(0.1);
        c.Select(x => Math.Round(x.RotationDeg)).Distinct().Should().HaveCount(4);
    }

    [Fact]
    public void ImageBounds_AddTheTemplateRotationToTheImagesOwn()
    {
        var img = new ImportImage { Center = new Vec2(0, 0), WidthM = 20, HeightM = 10, RotationDeg = 15 };
        var b = TemplatePlacer.ImageBounds(img, new Placement(0, 0, 350));

        b.RotationDeg.Should().BeApproximately(5, 1e-9);
        ((b.NeLat - b.SwLat) * 111_320).Should().BeApproximately(10, 1e-6);
        ((b.NeLng - b.SwLng) * 111_320).Should().BeApproximately(20, 1e-6);
    }
}

public class RoomPlanWindowsTests
{
    private static readonly double[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    private static List<double> At(double x, double y, double z) =>
        new() { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1 };

    [Fact]
    public void Window_MapsToPlanMeters_WithSillAndHeadAboveTheFloor()
    {
        var capture = new RoomPlanCapture
        {
            Floors = { new RoomPlanSurface { Transform = At(0, -1.2, 0), Dimensions = new() { 5, 5, 0 } } },
            Windows = { new RoomPlanSurface { Transform = At(1, 0.3, -2), Dimensions = new() { 0.9, 1.0, 0 } } },
        };

        var w = RoomPlanWindows.FromCapture(capture, Identity).Single();

        w.Center.Should().Be(new Vec2(1, 2)); // RoomPlan z points south of the plan
        w.WidthM.Should().Be(0.9);
        w.SillM.Should().BeApproximately(1.0, 1e-9);
        w.HeadM.Should().BeApproximately(2.0, 1e-9);
        w.IsDoor.Should().BeFalse();
    }

    [Fact]
    public void Read_MissingArchive_ReturnsNothing() =>
        RoomPlanWindows.Read(null, "{}").Should().BeEmpty();
}

public class SiteExteriorMaterialTests
{
    private static string Loop(string material) =>
        $$"""[{"points":[{"lat":0,"lng":0},{"lat":0,"lng":1},{"lat":1,"lng":1},{"lat":0,"lng":0}],"material":"{{material}}"}]""";

    [Fact]
    public void MostCommonExterior_Wins_ResidentialOnATie()
    {
        FloorPlanWallMaterials.SiteExteriorMaterial(new[] { Loop("exterior_commercial"), Loop("exterior_commercial"), Loop("exterior_residential") })
            .Should().Be("exterior_commercial");
        FloorPlanWallMaterials.SiteExteriorMaterial(new[] { Loop("exterior_commercial"), Loop("exterior_residential") })
            .Should().Be("exterior_residential");
        FloorPlanWallMaterials.SiteExteriorMaterial(new[] { Loop("brick"), null })
            .Should().Be("exterior_residential");
    }

    [Fact]
    public void OutlineOrHull_OpenedOutline_FallsBackToTheHullOfItsWalls()
    {
        // An outline split open by deleting one wall: two open polylines, no closed loop.
        const string opened = """[{"points":[{"lat":0,"lng":0},{"lat":0,"lng":2},{"lat":1,"lng":2}],"material":"brick"},{"points":[{"lat":1,"lng":0},{"lat":0,"lng":0}],"material":"brick"}]""";

        FloorPlanWallMaterials.OutlinePoints(opened).Should().BeNull();
        FloorPlanWallMaterials.OutlineOrHull(opened)!.Should().HaveCount(4);
    }

    [Fact]
    public void OutlinePoints_DropsTheClosingPoint() =>
        FloorPlanWallMaterials.OutlinePoints(Loop("brick"))!.Should().HaveCount(3);
}
