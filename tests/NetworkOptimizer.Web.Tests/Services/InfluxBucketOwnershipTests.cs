using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class InfluxBucketOwnershipTests
{
    private static Func<string, Task<(string? Primary, string? Longterm)>> Reader(
        Dictionary<string, (string? Primary, string? Longterm)> custom) =>
        slug => custom.TryGetValue(slug, out var b)
            ? Task.FromResult(b)
            : Task.FromResult<(string?, string?)>((null, null));

    [Fact]
    public async Task EverySiteOnTheInstallIsTaken_includingOnesTheCallerCannotSee()
    {
        // "hidden" stands for a site the caller has no membership on; the helper is given every slug.
        var taken = await InfluxBucketOwnership.GatherTakenAsync(
            null, null,
            new[] { "default", "site-x", "hidden", "custom" },
            currentSlug: "site-x",
            Reader(new() { ["custom"] = ("custom-primary", "custom-longterm") }));

        taken.Should().Contain(new[]
        {
            "hidden-network_monitoring", "hidden-network_monitoring_longterm",
            "custom-primary", "custom-longterm",
        });
    }

    [Fact]
    public async Task TheCurrentSitesOwnNamesAreNotTaken()
    {
        var taken = await InfluxBucketOwnership.GatherTakenAsync(
            null, null,
            new[] { "site-x", "other" },
            currentSlug: "site-x",
            Reader(new() { ["site-x"] = ("x-custom", "x-custom-lt") }));

        taken.Should().NotContain(new[]
        {
            "x-custom", "x-custom-lt", "site-x-network_monitoring", "site-x-network_monitoring_longterm"
        });
        taken.Should().Contain("other-network_monitoring");
    }

    [Fact]
    public async Task AnUnprovisionedSiteContributesOnlyItsDerivedNames()
    {
        var taken = await InfluxBucketOwnership.GatherTakenAsync(
            "main-primary", "main-longterm",
            new[] { "site-x", "fresh" },
            currentSlug: "site-x",
            _ => throw new InvalidOperationException("no such database"));

        taken.Should().BeEquivalentTo(new[]
        {
            "main-primary", "main-longterm", "fresh-main-primary", "fresh-main-longterm"
        });
    }

    [Fact]
    public async Task ComparisonIgnoresCase()
    {
        var taken = await InfluxBucketOwnership.GatherTakenAsync(
            null, null, new[] { "other" }, currentSlug: "site-x", Reader(new()));

        taken.Should().Contain("OTHER-Network_Monitoring");
    }
}
