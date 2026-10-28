using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class DeviceModelFilterTests
{
    private static IEnumerable<string> Devices(params (string Sku, int Count)[] skus) =>
        skus.SelectMany(s => Enumerable.Repeat(s.Sku, s.Count));

    private static List<string> Render(IEnumerable<DeviceModelFilterOption> options) =>
        options.Select(o => new string(' ', o.Depth * 2) + $"{o.Prefix} ({o.Count})").ToList();

    [Fact]
    public void LargeSite_PrunesSingleChildPrefixes()
    {
        var skus = Devices(
            ("U6-Extender", 15), ("U6-LR", 4), ("U6-Mesh", 3), ("U6-Mesh-Pro", 2), ("U6-Pro", 4),
            ("U7-Pro", 10), ("U7-Pro-Max", 2), ("U7-Pro-Outdoor", 4),
            ("UAP-AC-M", 2), ("UAP-AC-M-PRO", 6), ("UAP-AC-Pro", 5), ("UAP-IW-HD", 40),
            ("USW-24-PoE", 8), ("USW-Aggregation", 2), ("USW-Pro-24-PoE", 6),
            ("USW-Pro-Aggregation", 1), ("USW-Pro-HD-24", 1));

        Render(DeviceModelFilter.BuildOptions(skus)).Should().Equal(
            "U6 (28)",
            "  U6-Extender (15)",
            "  U6-LR (4)",
            "  U6-Mesh (5)",
            "    U6-Mesh-Pro (2)",
            "  U6-Pro (4)",
            "U7-Pro (16)",
            "  U7-Pro-Max (2)",
            "  U7-Pro-Outdoor (4)",
            "UAP (53)",
            "  UAP-AC (13)",
            "    UAP-AC-M (8)",
            "      UAP-AC-M-PRO (6)",
            "    UAP-AC-Pro (5)",
            "  UAP-IW-HD (40)",
            "USW (18)",
            "  USW-24-PoE (8)",
            "  USW-Aggregation (2)",
            "  USW-Pro (8)",
            "    USW-Pro-24-PoE (6)",
            "    USW-Pro-Aggregation (1)",
            "    USW-Pro-HD-24 (1)");
    }

    [Fact]
    public void SmallSite_CollapsesChainsToTheSku()
    {
        var skus = Devices(
            ("U6-IW", 1), ("U7-Pro", 2), ("UAP-AC-M", 1),
            ("USW-Enterprise-8-PoE", 1), ("USW-Pro-HD-24-PoE", 1));

        Render(DeviceModelFilter.BuildOptions(skus)).Should().Equal(
            "U6-IW (1)",
            "U7-Pro (2)",
            "UAP-AC-M (1)",
            "USW (2)",
            "  USW-Enterprise-8-PoE (1)",
            "  USW-Pro-HD-24-PoE (1)");
    }

    [Fact]
    public void EmptyAndNullSkus_AreIgnored()
    {
        DeviceModelFilter.BuildOptions(new string?[] { null, "", " " }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("U7-Pro", "U7-Pro", true)]
    [InlineData("U7-Pro-Max", "U7-Pro", true)]
    [InlineData("U7-Pro2", "U7-Pro", false)]
    [InlineData("UAP-AC-M-PRO", "UAP-AC-M", true)]
    [InlineData("u7-pro-max", "U7-Pro", true)]
    [InlineData(null, "U7", false)]
    public void Matches_IsTheSkuOrADashExtension(string? sku, string prefix, bool expected)
    {
        DeviceModelFilter.Matches(sku, prefix).Should().Be(expected);
    }

    [Fact]
    public void Label_IndentsWithNonBreakingSpaces()
    {
        new DeviceModelFilterOption("U6-Mesh", 1, 5).Label.Should().Be("   U6-Mesh (5)");
    }
}
