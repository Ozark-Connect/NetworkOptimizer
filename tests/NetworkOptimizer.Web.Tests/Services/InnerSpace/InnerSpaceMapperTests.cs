using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Web.Services.InnerSpace;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.InnerSpace;

public class InnerSpaceMapperTests
{
    // 100 shape units per meter throughout: a 4 m scale line is 400 units long.
    private const double Ppm = 100;

    private sealed class ProjectBuilder
    {
        public InnerSpaceProject Project { get; } = new();
        public Dictionary<string, (double, double)> ImageSizes { get; } = new();
        public Dictionary<string, ScanInfo> Scans { get; } = new();

        public ProjectBuilder Plan(string id, string title, int ordering = 0, bool scanned = false,
            string? dcProject = null, DateTime? created = null, double? attenuation = null)
        {
            Project.Plans.Add(new InnerSpacePlan
            {
                Id = id, Title = title, Ordering = ordering, DcSourceProjectId = dcProject, CreatedAt = created,
                Attenuation = attenuation,
            });
            Shape(new
            {
                type = "map", planId = id, urlImage = $"/proxy/innerspace/{id}/plan.png",
                urlModel = scanned ? $"/proxy/innerspace/{id}/scan.gltf" : null,
                position = new[] { new { x = 0.0, y = 0.0, z = 0.0 } }, rotation = 0.0,
            });
            Shape(new
            {
                type = "scale", planId = id, scale = 4.0, height = 2.5, defaultScale = false,
                position = new[] { new { x = 0.0, y = 0.0 }, new { x = 4 * Ppm, y = 0.0 } },
            });
            ImageSizes[id] = (1000, 800);
            return this;
        }

        public ProjectBuilder Wall(string planId, double x1, double y1, double x2, double y2,
            string variant = "concrete", string? wallTypeId = null)
        {
            Shape(new
            {
                type = "wall", id = Guid.NewGuid().ToString(), planId, variant, wallTypeId,
                position = new[] { new { x = x1 * Ppm, y = y1 * Ppm }, new { x = x2 * Ppm, y = y2 * Ppm } },
            });
            return this;
        }

        /// <summary>A closed rectangle in meters, counterclockwise from its lower left corner.</summary>
        public ProjectBuilder Rect(string planId, double x, double y, double w, double h, string variant = "concrete")
        {
            Wall(planId, x, y, x + w, y, variant);
            Wall(planId, x + w, y, x + w, y + h, variant);
            Wall(planId, x + w, y + h, x, y + h, variant);
            Wall(planId, x, y + h, x, y, variant);
            return this;
        }

        public ProjectBuilder Ap(string planId, string productId, string sku, bool managed, double x, double y,
            string? mac = null, double z = 0, string mount = "ceiling")
        {
            if (Project.Products.All(p => p.Id != productId))
                Project.Products.Add(new InnerSpaceProduct
                {
                    Id = productId, Sku = sku, Category = "wifi", Subcategory = "access_point", IsManaged = managed,
                });
            Shape(new
            {
                type = "device", planId, productId, title = sku, mount,
                position = new[] { new { x = x * Ppm, y = y * Ppm, z = z * Ppm } },
                meta = mac == null ? null : new { mac },
                rotation = new { pov = new { x = 0.0, y = 0.0, z = 0.0, w = 1.0 }, @base = new { x = 0.0, y = 0.0, z = 0.0, w = 1.0 } },
            });
            return this;
        }

        public ProjectBuilder Shape(object shape)
        {
            Project.Shapes.Add(JsonSerializer.SerializeToElement(shape));
            return this;
        }

        public List<ImportTemplate> Build(string exterior = "exterior_residential") =>
            InnerSpaceMapper.Build(Project, ImageSizes, Scans, exterior);
    }

    [Fact]
    public void Scan_UneditedOutlineTakesSiteExterior_AndInteriorTakesDrywall()
    {
        var b = new ProjectBuilder().Plan("p1", "Cabin", scanned: true)
            .Rect("p1", 0, 0, 4, 3)
            .Wall("p1", 2, 0, 2, 3); // interior partition

        var t = b.Build("exterior_commercial").Single();
        var floor = t.Floors.Single();

        var outline = floor.Walls.Single(w => w.IsClosed);
        outline.Materials.Should().OnlyContain(m => m == "exterior_commercial");
        floor.Walls.Single(w => !w.IsClosed).Materials.Should().Equal("drywall");
        floor.HeightM.Should().Be(2.5);
        t.Source.Should().Be(ImportSource.Scan);
    }

    [Fact]
    public void DrawnPlan_ConcreteIsTheAuthorsChoice()
    {
        var t = new ProjectBuilder().Plan("p1", "Office").Rect("p1", 0, 0, 4, 3).Build().Single();

        t.Floors.Single().Walls.SelectMany(w => w.Materials).Should().OnlyContain(m => m == "concrete");
        t.Source.Should().Be(ImportSource.Drawn);
    }

    [Fact]
    public void DrawnPlan_TJunctionWalls_BecomeOneClosedOutline_PlusInteriorWalls()
    {
        // Outer walls drawn as separate segments, two interior walls ending mid-wall (T-junctions),
        // and a stub wall poking out of the outline. Chaining alone never closes this.
        var t = new ProjectBuilder().Plan("p1", "Office")
            .Wall("p1", 0, 0, 10, 0, "brick").Wall("p1", 10, 0, 10, 6, "brick")
            .Wall("p1", 10, 6, 0, 6, "brick").Wall("p1", 0, 6, 0, 0, "brick")
            .Wall("p1", 4, 0, 4, 6, "drywall")   // full-height partition, T at both ends
            .Wall("p1", 4, 3, 10, 3, "drywall")  // T into the partition and the east wall
            .Wall("p1", 10, 2, 12, 2, "metal")   // stub outside
            .Build().Single();

        var floor = t.Floors.Single();
        var outline = floor.Walls.Single(w => w.IsClosed);
        PlanGeometry.Area(outline.Points).Should().BeApproximately(60, 1e-6);
        outline.Materials.Should().OnlyContain(m => m == "brick");
        floor.Footprints.Should().ContainSingle();
        floor.Walls.Where(w => !w.IsClosed).SelectMany(w => w.Materials)
            .Should().Contain("drywall").And.Contain("metal");
    }

    [Fact]
    public void Door_IsCutIntoItsHostWall_WithDoorSpan()
    {
        var t = new ProjectBuilder().Plan("p1", "Office")
            .Rect("p1", 0, 0, 4, 3, "drywall")
            .Wall("p1", 1, 0, 2, 0, "door_wood")
            .Build().Single();

        var outline = t.Floors.Single().Walls.Single(w => w.IsClosed);
        outline.Materials.Should().Contain("door_wood");
        outline.Materials.Count.Should().Be(6); // the bottom wall became three pieces
        var door = outline.Materials.IndexOf("door_wood");
        outline.Spans[door].Should().Be(InnerSpaceMaterials.DoorSpan);
        (outline.Points[door + 1] - outline.Points[door]).Length.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void ScannedWindow_IsCutWithItsMeasuredHeights_UnlessADrawnOpeningIsThere()
    {
        var b = new ProjectBuilder().Plan("p1", "Cabin", scanned: true).Rect("p1", 0, 0, 4, 3);
        b.Scans["p1"] = new ScanInfo(new List<ScannedOpening>
        {
            new(new Vec2(2, 3.08), 1.0, 1.0, 2.1, IsDoor: false),  // top wall, 8 cm off its centerline
        }, Story: null);

        var outline = b.Build().Single().Floors.Single().Walls.Single(w => w.IsClosed);
        var i = outline.Materials.IndexOf("window_2_pane");
        i.Should().BeGreaterThanOrEqualTo(0);
        outline.Spans[i].Should().Be(new WallSpan(1.0, 2.1));
    }

    [Fact]
    public void Scale_AuthorsLineWinsOverThePlaceholder()
    {
        var b = new ProjectBuilder().Plan("p1", "Office").Rect("p1", 0, 0, 4, 3);
        // A placeholder line claiming 400 units = 1 m would make the room 16 m wide.
        b.Shape(new
        {
            type = "scale", planId = "p1", scale = 1.0, height = 2.7, defaultScale = true,
            position = new[] { new { x = 0.0, y = 0.0 }, new { x = 400.0, y = 0.0 } },
        });

        var outline = b.Build().Single().Floors.Single().Walls.Single(w => w.IsClosed);
        var (min, max) = PlanGeometry.Bounds(outline.Points);
        (max.X - min.X).Should().BeApproximately(4.0, 1e-6);
    }

    [Fact]
    public void Aps_AdoptedReplacesSamePlaceholder_AndKeepsMacAndHeight()
    {
        var t = new ProjectBuilder().Plan("p1", "Office").Rect("p1", 0, 0, 10, 6)
            .Ap("p1", "prod-planned", "U7-Pro", managed: false, 3, 3)
            .Ap("p1", "prod-adopted", "U7-Pro", managed: true, 3.2, 3, mac: "AA-BB-CC-DD-EE-FF", z: 2.4)
            .Ap("p1", "prod-planned-2", "E7", managed: false, 8, 3)
            .Build().Single();

        var aps = t.Floors.Single().Aps;
        aps.Should().HaveCount(2);
        var adopted = aps.Single(a => a.Mac != null);
        adopted.Mac.Should().Be("aa:bb:cc:dd:ee:ff");
        adopted.HeightM.Should().BeApproximately(2.4, 1e-6);
        aps.Single(a => a.Mac == null).Model.Should().Be("E7");
    }

    [Fact]
    public void Aps_AntennaModesOutsideOurPatterns_FallBackToTheModelDefault()
    {
        var b = new ProjectBuilder().Plan("p1", "Office").Rect("p1", 0, 0, 10, 6);
        b.Project.Products.Add(new InnerSpaceProduct { Id = "e7a", Sku = "E7-Audience", Category = "wifi", Subcategory = "access_point" });
        b.Shape(new { type = "device", planId = "p1", productId = "e7a", title = "Wide", antenna = "wide", mount = "wall",
            position = new[] { new { x = 100.0, y = 100.0, z = 0.0 } } });
        b.Shape(new { type = "device", planId = "p1", productId = "e7a", title = "Dual", antenna = "dual", mount = "wall",
            position = new[] { new { x = 500.0, y = 100.0, z = 0.0 } } });

        var aps = b.Build().Single().Floors[0].Aps;

        aps.Single(a => a.Name == "Wide").AntennaMode.Should().Be("wide");
        aps.Single(a => a.Name == "Dual").AntennaMode.Should().BeNull();
        aps.Should().OnlyContain(a => a.MountType == "wall");
    }

    [Theory]
    // Design Center's wall-mounted E7-Audience on a south wall: 90 degrees about X, faces south.
    [InlineData(-0.7071067811865475, -0.7071067811865475, 0, 0, 180)]
    // The same mount turned to the north wall: 90 degrees the other way about X, faces north.
    [InlineData(0.7071067811865475, -0.7071067811865475, 0, 0, 0)]
    // Tilted to face east: -90 degrees about Y.
    [InlineData(0.7071067811865475, 0, 0.7071067811865475, 0, 90)]
    public void Azimuth_WallMountedAp_FacesWhereItsBoresightPoints(double w, double x, double y, double z, int expected) =>
        InnerSpaceMapper.Azimuth(new InnerSpaceQuaternion { W = w, X = x, Y = y, Z = z }).Should().Be(expected);

    [Fact]
    public void Azimuth_CeilingAp_KeepsInnerSpacesIntegrationAzimuth()
    {
        // Pointing straight down (180 degrees about X), as the tiny home's AP: InnerSpace says ~269.
        var az = InnerSpaceMapper.Azimuth(new InnerSpaceQuaternion { W = 0.0000872621869419067, X = 0.9999510020325467, Y = -0.0000008638318394273384, Z = 0.00989878369651731 });
        az.Should().BeInRange(265, 272);
    }

    [Fact]
    public void Building_MatchingFloorsStackIntoOneTemplate()
    {
        var t = new ProjectBuilder()
            .Plan("f1", "Ground", ordering: 1).Rect("f1", 0, 0, 10, 8)
            .Plan("f2", "Upstairs", ordering: 2).Rect("f2", 0, 0, 10, 8)
            .Build();

        t.Should().ContainSingle();
        t[0].Floors.Select(f => f.FloorNumber).Should().Equal(1, 2);
    }

    [Fact]
    public void Building_HouseAndDetachedGarage_SplitByOutline_UpstairsJoinsTheHouse()
    {
        // Ground floor: a house and a separate garage. Upstairs sits over the house only.
        var t = new ProjectBuilder()
            .Plan("f1", "Ground", ordering: 1).Rect("f1", 0, 0, 12, 10).Rect("f1", 20, 0, 6, 6)
            .Plan("f2", "Upstairs", ordering: 2).Rect("f2", 0, 0, 12, 10)
            .Build();

        var parts = t.Where(x => x.WasSplit).ToList();
        parts.Should().HaveCount(2);
        var house = parts.Single(x => x.Floors.Count == 2);
        house.Floors.Select(f => f.FloorNumber).Should().Equal(1, 2);
        var garage = parts.Single(x => x.Floors.Count == 1);
        PlanGeometry.Area(garage.Floors[0].Footprints[0]).Should().BeApproximately(36, 1e-6);

        // Import as one building: the whole, with every wall, shares the parts' group.
        var whole = t.Single(x => x.IsWhole);
        whole.Floors.Should().HaveCount(2);
        whole.WallSegmentCount.Should().Be(parts.Sum(p => p.WallSegmentCount));
        parts.Should().OnlyContain(p => p.GroupId == whole.GroupId);
    }

    [Fact]
    public void Building_AlignedStackWithOneOutlinePerFloor_IsOneBuilding_AtAnySize()
    {
        // A small cabin as "floor 1" under a much bigger outline as "floor 2", aligned over it:
        // one outline per floor, overlapping, so one building.
        var t = new ProjectBuilder()
            .Plan("f1", "Cabin", ordering: 1).Rect("f1", 0, 0, 3, 8)
            .Plan("f2", "House", ordering: 2).Rect("f2", -6, -4, 15, 16)
            .Build();

        var only = t.Should().ContainSingle().Subject;
        only.WasSplit.Should().BeFalse();
        only.Floors.Select(f => f.FloorNumber).Should().Equal(1, 2);
        only.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void UnclosedTracedOutline_StillGetsAFootprint_AndKeepsItsMaterials()
    {
        // The house outline misses closing by a 3 m gap, as a hand trace in InnerSpace does.
        var t = new ProjectBuilder()
            .Plan("f1", "Cabin", ordering: 1).Rect("f1", 0, 0, 3, 8)
            .Plan("f2", "House", ordering: 2)
            .Wall("f2", -6, -4, 9, -4).Wall("f2", 9, -4, 9, 12).Wall("f2", 9, 12, -6, 12).Wall("f2", -6, 12, -6, -1)
            .Build().Single();

        var house = t.Floors.Single(f => f.Label == "House");
        house.Footprints.Should().ContainSingle(); // the walls' hull, for snapping
        house.Walls.Should().ContainSingle().Which.Materials.Should().OnlyContain(m => m == "concrete");
    }

    [Fact]
    public void Canvas_TwoSeparateOutlines_BecomeTwoBuildings()
    {
        var t = new ProjectBuilder().Plan("p1", "Site")
            .Rect("p1", 0, 0, 10, 8)
            .Rect("p1", 20, 0, 6, 6)
            .Ap("p1", "prod", "U7-Pro", managed: false, 23, 3)
            .Build();

        var parts = t.Where(x => x.WasSplit).ToList();
        parts.Should().HaveCount(2);
        parts.Sum(x => x.ApCount).Should().Be(1);
        parts.Single(x => x.ApCount == 1).Floors[0].Footprints.Single()
            .Should().HaveCount(5); // the 6 x 6 building's closed loop
        t.Single(x => x.IsWhole).ApCount.Should().Be(1);
    }

    [Fact]
    public void DesignCenter_OlderImportOfTheSameProject_IsMarked()
    {
        var t = new ProjectBuilder()
            .Plan("a", "Demo", dcProject: "dc-1", created: new DateTime(2026, 1, 1)).Rect("a", 0, 0, 4, 3)
            .Plan("b", "Demo (2)", dcProject: "dc-1", created: new DateTime(2026, 2, 1)).Rect("b", 0, 0, 4, 3)
            .Build();

        t.Single(x => x.Name == "Demo").IsEarlierImport.Should().BeTrue();
        t.Single(x => x.Name == "Demo (2)").IsEarlierImport.Should().BeFalse();
        t.Should().OnlyContain(x => x.Source == ImportSource.DesignCenter);
    }

    [Fact]
    public void Template_IsCenteredOnItsFootprint_AndImageFollows()
    {
        var t = new ProjectBuilder().Plan("p1", "Office").Rect("p1", 10, 10, 4, 2).Build().Single();

        var (min, max) = PlanGeometry.Bounds(t.Floors[0].Footprints[0]);
        ((min.X + max.X) / 2).Should().BeApproximately(0, 1e-9);
        ((min.Y + max.Y) / 2).Should().BeApproximately(0, 1e-9);
        // The image was centered on the shape origin, 12 m west and 11 m south of the building center.
        t.Floors[0].Image!.Center.X.Should().BeApproximately(-12, 1e-9);
        t.Floors[0].Image!.WidthM.Should().BeApproximately(10, 1e-9);
    }

    [Fact]
    public void PlanWithoutScale_IsSkippedWithAWarning()
    {
        var b = new ProjectBuilder().Plan("p1", "Office").Rect("p1", 0, 0, 4, 3);
        b.Project.Plans.Add(new InnerSpacePlan { Id = "p2", Title = "Unscaled" });

        var t = b.Build();

        t.Should().ContainSingle();
        t[0].Warnings.Should().Contain(w => w.Contains("Unscaled"));
    }

    [Fact]
    public void SlabLoss_MapsToTheNearestFloorMaterial()
    {
        new ProjectBuilder().Plan("p1", "Office", ordering: 1, attenuation: 10).Rect("p1", 0, 0, 4, 3)
            .Build().Single().Floors[0].FloorMaterial.Should().Be("floor_wood");
        new ProjectBuilder().Plan("p1", "Office", ordering: 1, attenuation: 20).Rect("p1", 0, 0, 4, 3)
            .Build().Single().Floors[0].FloorMaterial.Should().Be("floor_concrete");
    }
}
