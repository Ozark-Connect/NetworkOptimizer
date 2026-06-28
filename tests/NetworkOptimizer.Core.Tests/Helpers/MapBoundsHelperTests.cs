using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class MapBoundsHelperTests
{
    // A real home location (used as a stand-in for any placed content).
    private const double HomeLat = 51.0;
    private const double HomeLng = 7.0;

    // The app's default map center - what a building created before any AP placement
    // inherits, and the coordinate that used to drag the fit across the globe.
    private const double DefaultLat = 38.0;
    private const double DefaultLng = -92.0;

    #region IsPositioned

    [Theory]
    [InlineData(51.0, 7.0, true)]      // real location
    [InlineData(38.0, -92.0, true)]    // default center is still a valid coordinate
    [InlineData(0.0, 0.0, false)]      // null island
    [InlineData(0.00005, -0.00005, false)] // effectively null island
    [InlineData(91.0, 7.0, false)]     // latitude out of range
    [InlineData(51.0, 181.0, false)]   // longitude out of range
    public void IsPositioned_ClassifiesCoordinates(double lat, double lng, bool expected)
    {
        MapBoundsHelper.IsPositioned(lat, lng).Should().Be(expected);
    }

    #endregion

    #region ComputeBuildingBounds

    [Fact]
    public void ComputeBuildingBounds_PrefersWalls_WhenPresent()
    {
        var walls = new[] { (HomeLat, HomeLng), (HomeLat + 0.001, HomeLng + 0.001) };
        // A bare floor at the default center must be ignored once walls exist.
        var floors = new[] { Bare(DefaultLat, DefaultLng) };

        var bounds = MapBoundsHelper.ComputeBuildingBounds(walls, floors);

        bounds.Should().NotBeNull();
        bounds!.Value.SwLat.Should().BeApproximately(HomeLat, 1e-9);
        bounds.Value.NeLat.Should().BeApproximately(HomeLat + 0.001, 1e-9);
    }

    [Fact]
    public void ComputeBuildingBounds_UsesContentFloors_WhenNoWalls()
    {
        var floors = new[]
        {
            Content(HomeLat - 0.001, HomeLng - 0.001, HomeLat + 0.001, HomeLng + 0.001),
            Bare(DefaultLat, DefaultLng), // placeholder floor must not extend the fit
        };

        var bounds = MapBoundsHelper.ComputeBuildingBounds(NoWalls(), floors);

        bounds.Should().NotBeNull();
        bounds!.Value.SwLat.Should().BeApproximately(HomeLat - 0.001, 1e-9);
        bounds.Value.NeLat.Should().BeApproximately(HomeLat + 0.001, 1e-9);
        bounds.Value.SwLng.Should().BeApproximately(HomeLng - 0.001, 1e-9);
    }

    [Fact]
    public void ComputeBuildingBounds_ReturnsNull_ForBareFloorsOnly()
    {
        var floors = new[] { Bare(DefaultLat, DefaultLng), Bare(DefaultLat, DefaultLng) };

        MapBoundsHelper.ComputeBuildingBounds(NoWalls(), floors).Should().BeNull();
    }

    [Fact]
    public void ComputeBuildingBounds_ReturnsNull_WhenEmpty()
    {
        MapBoundsHelper.ComputeBuildingBounds(NoWalls(), System.Array.Empty<MapBoundsHelper.FloorRecord>())
            .Should().BeNull();
    }

    #endregion

    #region ComputeAllContentBounds

    /// <summary>
    /// The reported regression: a German user with placed APs at home but a building/floor
    /// created earlier at the central-US default. The fit must frame the APs, not span the
    /// Atlantic to the placeholder floor.
    /// </summary>
    [Fact]
    public void ComputeAllContentBounds_IgnoresPlaceholderFloor_WhenApsPlaced()
    {
        var aps = new[] { (HomeLat, HomeLng) };
        var bareFloorAtDefault = new[] { Bare(DefaultLat, DefaultLng) };

        var bounds = MapBoundsHelper.ComputeAllContentBounds(aps, NoWalls(), bareFloorAtDefault);

        bounds.Should().NotBeNull();
        bounds!.Value.SwLat.Should().BeApproximately(HomeLat, 1e-9);
        bounds.Value.NeLat.Should().BeApproximately(HomeLat, 1e-9);
        bounds.Value.SwLng.Should().BeApproximately(HomeLng, 1e-9);
        bounds.Value.NeLng.Should().BeApproximately(HomeLng, 1e-9);
    }

    [Fact]
    public void ComputeAllContentBounds_ExtendsToContentFloor()
    {
        var aps = new[] { (HomeLat, HomeLng) };
        var floors = new[] { Content(HomeLat + 0.01, HomeLng + 0.01, HomeLat + 0.02, HomeLng + 0.02) };

        var bounds = MapBoundsHelper.ComputeAllContentBounds(aps, NoWalls(), floors);

        bounds.Should().NotBeNull();
        bounds!.Value.SwLat.Should().BeApproximately(HomeLat, 1e-9);
        bounds.Value.NeLat.Should().BeApproximately(HomeLat + 0.02, 1e-9);
    }

    [Fact]
    public void ComputeAllContentBounds_ExcludesNullIslandAps()
    {
        var aps = new[] { (HomeLat, HomeLng), (0.0, 0.0) };

        var bounds = MapBoundsHelper.ComputeAllContentBounds(aps, NoWalls(),
            System.Array.Empty<MapBoundsHelper.FloorRecord>());

        bounds.Should().NotBeNull();
        bounds!.Value.SwLat.Should().BeApproximately(HomeLat, 1e-9);
        bounds.Value.NeLat.Should().BeApproximately(HomeLat, 1e-9);
    }

    [Fact]
    public void ComputeAllContentBounds_FramesContentFloor_WhenNoAps()
    {
        var floors = new[] { Content(HomeLat - 0.001, HomeLng - 0.001, HomeLat + 0.001, HomeLng + 0.001) };

        var bounds = MapBoundsHelper.ComputeAllContentBounds(
            System.Array.Empty<(double, double)>(), NoWalls(), floors);

        bounds.Should().NotBeNull();
        bounds!.Value.SwLat.Should().BeApproximately(HomeLat - 0.001, 1e-9);
    }

    [Fact]
    public void ComputeAllContentBounds_ReturnsNull_WhenNothingPlaced()
    {
        var bounds = MapBoundsHelper.ComputeAllContentBounds(
            System.Array.Empty<(double, double)>(),
            NoWalls(),
            new[] { Bare(DefaultLat, DefaultLng) });

        bounds.Should().BeNull();
    }

    #endregion

    private static (double, double)[] NoWalls() => System.Array.Empty<(double, double)>();

    private static MapBoundsHelper.FloorRecord Bare(double lat, double lng) =>
        new(lat - 0.0002, lng - 0.0002, lat + 0.0002, lng + 0.0002, HasContent: false);

    private static MapBoundsHelper.FloorRecord Content(double swLat, double swLng, double neLat, double neLng) =>
        new(swLat, swLng, neLat, neLng, HasContent: true);
}
