using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class FloorPlanWallMaterialsTests
{
    // A closed rectangle: five points, last equals first.
    private static string Closed(string material) =>
        "[{\"points\":[{\"lat\":1,\"lng\":1},{\"lat\":1,\"lng\":2},{\"lat\":2,\"lng\":2},{\"lat\":2,\"lng\":1},{\"lat\":1,\"lng\":1}],\"material\":\"" + material + "\"}]";

    // Three sides of the same rectangle: not closed.
    private static string Open(string material) =>
        "[{\"points\":[{\"lat\":1,\"lng\":1},{\"lat\":1,\"lng\":2},{\"lat\":2,\"lng\":2},{\"lat\":2,\"lng\":1}],\"material\":\"" + material + "\"}]";

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("[]", false)]
    [InlineData("not json", false)]
    public void Empty_or_unreadable_walls_have_no_outline(string? json, bool expected) =>
        FloorPlanWallMaterials.HasExteriorOutline(json).Should().Be(expected);

    [Fact]
    public void A_closed_exterior_shape_is_an_outline() =>
        FloorPlanWallMaterials.HasExteriorOutline(Closed("exterior_commercial")).Should().BeTrue();

    [Fact]
    public void An_outline_drawn_in_pieces_is_not_an_outline_until_closed() =>
        FloorPlanWallMaterials.HasExteriorOutline(Open("exterior_residential")).Should().BeFalse();

    [Fact]
    public void A_closed_interior_shape_is_not_an_outline() =>
        FloorPlanWallMaterials.HasExteriorOutline(Closed("drywall")).Should().BeFalse();

    [Fact]
    public void A_closed_shape_with_one_exterior_segment_is_an_outline()
    {
        var json = "[{\"points\":[{\"lat\":1,\"lng\":1},{\"lat\":1,\"lng\":2},{\"lat\":2,\"lng\":2},{\"lat\":1,\"lng\":1}]," +
                   "\"material\":\"drywall\",\"materials\":[\"drywall\",\"exterior_residential\",\"window\"]}]";
        FloorPlanWallMaterials.HasExteriorOutline(json).Should().BeTrue();
    }

    [Fact]
    public void The_legacy_exterior_key_counts_as_exterior() =>
        FloorPlanWallMaterials.HasExteriorOutline(Closed("exterior")).Should().BeTrue();

    [Fact]
    public void A_picked_exterior_wins_over_saved_floors() =>
        FloorPlanWallMaterials.BuildingExterior("exterior_commercial", new[] { Closed("exterior_residential") })
            .Should().Be("exterior_commercial");

    [Fact]
    public void A_new_floor_takes_the_exterior_type_saved_on_the_building() =>
        FloorPlanWallMaterials.BuildingExterior(null, new[] { Closed("exterior_commercial"), null, "[]" })
            .Should().Be("exterior_commercial");

    [Fact]
    public void The_most_common_saved_exterior_type_wins()
    {
        var mixed = "[{\"points\":[{\"lat\":1,\"lng\":1},{\"lat\":1,\"lng\":2},{\"lat\":2,\"lng\":2},{\"lat\":1,\"lng\":1}]," +
                    "\"material\":\"exterior_residential\",\"materials\":[\"exterior_commercial\",\"exterior_commercial\",\"exterior_residential\"]}]";
        FloorPlanWallMaterials.BuildingExterior(null, new[] { mixed }).Should().Be("exterior_commercial");
    }

    [Fact]
    public void A_building_with_no_exterior_walls_defaults_to_residential() =>
        FloorPlanWallMaterials.BuildingExterior(null, new[] { Closed("drywall"), null }).Should().Be("exterior_residential");

    [Fact]
    public void Draw_Layout_starts_on_the_exterior_type_until_the_outline_exists()
    {
        FloorPlanWallMaterials.DrawStartMaterial(null, "exterior_commercial", "window").Should().Be("exterior_commercial");
        FloorPlanWallMaterials.DrawStartMaterial(Open("exterior_commercial"), "exterior_commercial", "window").Should().Be("exterior_commercial");
    }

    [Fact]
    public void Draw_Layout_starts_on_the_last_interior_material_once_the_outline_exists()
    {
        FloorPlanWallMaterials.DrawStartMaterial(Closed("exterior_residential"), "exterior_residential", "door_wood").Should().Be("door_wood");
        FloorPlanWallMaterials.DrawStartMaterial(Closed("exterior_residential"), "exterior_residential", null).Should().Be("drywall");
    }

    [Fact]
    public void Closing_the_outline_with_an_exterior_selected_switches_to_interior() =>
        FloorPlanWallMaterials.OutlineJustClosed("exterior_residential", Open("exterior_residential"), Closed("exterior_residential"))
            .Should().BeTrue();

    [Fact]
    public void A_save_after_the_outline_exists_does_not_switch_again() =>
        FloorPlanWallMaterials.OutlineJustClosed("exterior_residential", Closed("exterior_residential"), Closed("exterior_residential"))
            .Should().BeFalse();

    [Fact]
    public void Closing_the_outline_with_an_interior_selected_keeps_it() =>
        FloorPlanWallMaterials.OutlineJustClosed("window", Open("exterior_residential"), Closed("exterior_residential"))
            .Should().BeFalse();
}
