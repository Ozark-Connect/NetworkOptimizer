using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Where a WAN-scoped report should send someone who has nothing to look at yet. Discovery is not
/// always the answer: a secondary WAN is traced THROUGH its context, so a WAN without one cannot
/// be discovered however many times you run it, and pointing there wastes the trip.
/// </summary>
public class WanDeepLinkTargetTests
{
    private static bool NeedsContextFirst(bool isPrimary, bool hasContext) => !isPrimary && !hasContext;

    [Theory]
    [InlineData(true, false, false)]   // the primary needs no context - discovery is the answer
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]   // secondary WITH a context - discovery is the answer
    [InlineData(false, false, true)]   // secondary with none - the context comes first
    public void ASecondaryWanWithoutAContextIsSentToMakeOne(bool isPrimary, bool hasContext, bool expected)
    {
        NeedsContextFirst(isPrimary, hasContext).Should().Be(expected);
    }

    private static string DiscoveryWanQuery(string? wanKey, int wanCount) =>
        string.IsNullOrEmpty(wanKey) || wanCount <= 1 ? "" : $"&wan={System.Uri.EscapeDataString(wanKey)}";

    [Fact]
    public void ADiscoveryLinkCarriesTheWanTheReportIsAbout()
    {
        DiscoveryWanQuery("wan2", 2).Should().Be("&wan=wan2");
    }

    [Fact]
    public void ASingleWanSiteAddsNothing()
    {
        // One WAN means one discovery; a parameter would only be noise in the address bar.
        DiscoveryWanQuery("wan", 1).Should().BeEmpty();
    }
}
