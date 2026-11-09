using FluentAssertions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

/// <summary>
/// The default site reaches delivery as a null slug, so its channels are not added twice (as the
/// site's own and again as the main site's) and its copies carry no "[main]" label.
/// </summary>
public class AlertSiteSlugTests
{
    private static AlertEvent Event(string? slug) => new()
    {
        EventType = "change.plan_reverted", Severity = AlertSeverity.Info, Source = "change_control", Title = "Plan reverted", SiteSlug = slug,
    };

    [Theory]
    [InlineData("main")]
    [InlineData("Main")]
    [InlineData(null)]
    public void DefaultSite_IsNull(string? slug) =>
        AlertProcessingService.WithDefaultSiteAsNull(Event(slug)).SiteSlug.Should().BeNull();

    [Fact]
    public void OtherSite_IsKept() =>
        AlertProcessingService.WithDefaultSiteAsNull(Event("site-b")).SiteSlug.Should().Be("site-b");
}
