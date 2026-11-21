using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class FloorPlanWallMaterialsTests
{
    // A closed square of the given side, starting at (0,0): five points, last equals first.
    private static string Square(string material, double side = 1, string? materials = null) =>
        "{\"points\":[{\"lat\":0,\"lng\":0},{\"lat\":0,\"lng\":" + side + "},{\"lat\":" + side + ",\"lng\":" + side + "}," +
        "{\"lat\":" + side + ",\"lng\":0},{\"lat\":0,\"lng\":0}],\"material\":\"" + material + "\"" +
        (materials == null ? "" : ",\"materials\":" + materials) + "}";

    // Three sides of the same square: not closed.
    private static string Open(string material) =>
        "{\"points\":[{\"lat\":0,\"lng\":0},{\"lat\":0,\"lng\":1},{\"lat\":1,\"lng\":1},{\"lat\":1,\"lng\":0}],\"material\":\"" + material + "\"}";

    private static string Floor(params string[] walls) => "[" + string.Join(",", walls) + "]";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Empty_or_unreadable_walls_have_no_outline(string? json) =>
        FloorPlanWallMaterials.HasOutline(json).Should().BeFalse();

    [Fact]
    public void A_closed_exterior_shape_is_an_outline() =>
        FloorPlanWallMaterials.HasOutline(Floor(Square("exterior_commercial"))).Should().BeTrue();

    [Fact]
    public void A_shed_outline_of_any_material_is_an_outline() =>
        FloorPlanWallMaterials.HasOutline(Floor(Square("metal"))).Should().BeTrue();

    [Fact]
    public void An_outline_drawn_in_pieces_is_not_an_outline_until_closed() =>
        FloorPlanWallMaterials.HasOutline(Floor(Open("exterior_residential"))).Should().BeFalse();

    [Fact]
    public void A_picked_outline_material_wins_over_saved_floors() =>
        FloorPlanWallMaterials.BuildingOutlineMaterial("metal", new[] { Floor(Square("exterior_residential")) })
            .Should().Be("metal");

    [Fact]
    public void A_new_floor_takes_the_outline_material_saved_on_the_building()
    {
        FloorPlanWallMaterials.BuildingOutlineMaterial(null, new[] { Floor(Square("exterior_commercial")), null, "[]" })
            .Should().Be("exterior_commercial");
        FloorPlanWallMaterials.BuildingOutlineMaterial(null, new[] { Floor(Square("metal")) })
            .Should().Be("metal");
    }

    [Fact]
    public void The_outline_is_the_largest_closed_shape_not_an_interior_room()
    {
        // Three small drywall rooms inside a large metal outline: the outline material is metal.
        var floor = Floor(Square("metal", 10), Square("drywall", 1), Square("drywall", 2), Square("drywall", 3));
        FloorPlanWallMaterials.BuildingOutlineMaterial(null, new[] { floor }).Should().Be("metal");
    }

    [Fact]
    public void A_door_or_window_cut_into_the_outline_does_not_change_its_material()
    {
        var floor = Floor(Square("exterior_residential", 1, "[\"exterior_residential\",\"window\",\"exterior_residential\",\"door_wood\"]"));
        FloorPlanWallMaterials.BuildingOutlineMaterial(null, new[] { floor }).Should().Be("exterior_residential");
    }

    [Fact]
    public void The_most_common_outline_material_across_floors_wins() =>
        FloorPlanWallMaterials.BuildingOutlineMaterial(null, new[]
        {
            Floor(Square("exterior_commercial")), Floor(Square("exterior_commercial")), Floor(Square("exterior_residential"))
        }).Should().Be("exterior_commercial");

    [Fact]
    public void A_building_with_no_outline_defaults_to_residential_exterior() =>
        FloorPlanWallMaterials.BuildingOutlineMaterial(null, new[] { Floor(Open("metal")), null })
            .Should().Be("exterior_residential");

    [Fact]
    public void Draw_Layout_starts_on_the_outline_material_until_the_outline_exists()
    {
        FloorPlanWallMaterials.DrawStartMaterial(null, "metal", "window").Should().Be("metal");
        FloorPlanWallMaterials.DrawStartMaterial(Floor(Open("metal")), "metal", "window").Should().Be("metal");
    }

    [Fact]
    public void Draw_Layout_starts_on_the_last_interior_material_once_the_outline_exists()
    {
        FloorPlanWallMaterials.DrawStartMaterial(Floor(Square("metal")), "metal", "door_wood").Should().Be("door_wood");
        FloorPlanWallMaterials.DrawStartMaterial(Floor(Square("exterior_residential")), "exterior_residential", null).Should().Be("drywall");
    }

    [Fact]
    public void Closing_the_outline_switches_to_interior_whatever_its_material()
    {
        FloorPlanWallMaterials.OutlineJustClosed(Floor(Open("metal")), Floor(Square("metal"))).Should().BeTrue();
        FloorPlanWallMaterials.OutlineJustClosed("[]", Floor(Square("exterior_residential"))).Should().BeTrue();
    }

    // An existing outbuilding saved before this change: one closed wood-paneling outline with two metal
    // doors cut in, the shape found on a real garage. It was not recognized as having an outline, so
    // Draw Layout started on an exterior type instead of the interior material.
    private const string ExistingGarage =
        "[{\"points\":[{\"lat\":0,\"lng\":0},{\"lat\":0,\"lng\":1},{\"lat\":0,\"lng\":1.4},{\"lat\":0,\"lng\":3}," +
        "{\"lat\":2,\"lng\":3},{\"lat\":2,\"lng\":1.5},{\"lat\":2,\"lng\":1.2},{\"lat\":2,\"lng\":0},{\"lat\":0,\"lng\":0}]," +
        "\"material\":\"wood_paneling\",\"materials\":[\"wood_paneling\",\"door_metal\",\"wood_paneling\",\"wood_paneling\"," +
        "\"wood_paneling\",\"door_metal\",\"wood_paneling\",\"wood_paneling\"]}]";

    [Fact]
    public void An_existing_wood_paneling_garage_has_its_outline() =>
        FloorPlanWallMaterials.HasOutline(ExistingGarage).Should().BeTrue();

    [Fact]
    public void Draw_Layout_on_an_existing_garage_starts_on_the_interior_material()
    {
        var outline = FloorPlanWallMaterials.BuildingOutlineMaterial(null, new[] { ExistingGarage });
        FloorPlanWallMaterials.DrawStartMaterial(ExistingGarage, outline, null).Should().Be("drywall");
        FloorPlanWallMaterials.DrawStartMaterial(ExistingGarage, outline, "door_metal").Should().Be("door_metal");
    }

    [Fact]
    public void A_new_floor_on_an_existing_garage_starts_on_wood_paneling()
    {
        var outline = FloorPlanWallMaterials.BuildingOutlineMaterial(null, new[] { ExistingGarage });
        outline.Should().Be("wood_paneling");
        FloorPlanWallMaterials.DrawStartMaterial("[]", outline, "door_metal").Should().Be("wood_paneling");
    }

    [Fact]
    public void A_save_after_the_outline_exists_does_not_switch_again() =>
        FloorPlanWallMaterials.OutlineJustClosed(Floor(Square("metal")), Floor(Square("metal"), Square("drywall", 0.5)))
            .Should().BeFalse();
}
