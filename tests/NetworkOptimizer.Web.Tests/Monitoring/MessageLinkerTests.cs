using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Turning a phrase inside an unavailable-reason into a link. The three real reasons the WAN Speed
/// Test page shows are used verbatim, because the point of these is that none of them loses text or
/// loses its link when the phrase changes.
/// </summary>
public class MessageLinkerTests
{
    private const string NoAgent =
        "WAN speed tests on a managed site run from its on-site agent, and this site has none online. " +
        "Set up the site's agent under Settings > Multi-Site to enable them.";

    private const string AgentOffline =
        "This site connects through its on-site agent, which isn't online. WAN speed tests can resume when it reconnects.";

    private const string NoGatewaySsh =
        "Gateway SSH not configured. Set it up in Settings, under the Connection tab.";

    [Fact]
    public void CaseA_NoAgent_LinksTheWholeBreadcrumbToTheSite()
    {
        var linked = MessageLinker.Split(NoAgent, "Settings > Multi-Site", "/settings?tab=multisite&configure=atl-1365");

        linked.HasLink.Should().BeTrue();
        linked.LinkText.Should().Be("Settings > Multi-Site");
        linked.Href.Should().Be("/settings?tab=multisite&configure=atl-1365");
        (linked.Before + linked.LinkText + linked.After).Should().Be(NoAgent);
    }

    [Fact]
    public void CaseB_AgentOffline_RendersPlainBecauseThereIsNowhereToGo()
    {
        var linked = MessageLinker.Split(AgentOffline, null, null);

        linked.HasLink.Should().BeFalse();
        linked.Before.Should().Be(AgentOffline);
    }

    [Fact]
    public void CaseC_NoGatewaySsh_LinksItsOwnPhraseToItsOwnPage()
    {
        var linked = MessageLinker.Split(NoGatewaySsh, "Settings, under the Connection tab", "/settings#gateway-ssh");

        linked.HasLink.Should().BeTrue();
        linked.LinkText.Should().Be("Settings, under the Connection tab");
        linked.Href.Should().Be("/settings#gateway-ssh");
        (linked.Before + linked.LinkText + linked.After).Should().Be(NoGatewaySsh);
    }

    [Fact]
    public void APhraseThatIsNotInTheMessageLosesTheLinkAndNothingElse()
    {
        // Copy gets edited; the link should fall away rather than the sentence.
        var linked = MessageLinker.Split(NoAgent, "Settings > Monitoring", "/settings?tab=multisite");

        linked.HasLink.Should().BeFalse();
        linked.Before.Should().Be(NoAgent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoHrefMeansNoLink(string? href)
    {
        var linked = MessageLinker.Split(NoAgent, "Settings > Multi-Site", href);

        linked.HasLink.Should().BeFalse();
        linked.Before.Should().Be(NoAgent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyMessageStaysEmpty(string? message)
    {
        var linked = MessageLinker.Split(message, "Settings > Multi-Site", "/settings?tab=multisite");

        linked.HasLink.Should().BeFalse();
        linked.Before.Should().Be(message);
    }

    [Fact]
    public void TheMessageIsOnlyEverDividedNeverEdited()
    {
        // Every branch must reassemble to the original - the renderer emits these three parts and
        // nothing else, so anything lost here is lost on screen.
        foreach (var (message, phrase, href) in new[]
                 {
                     (NoAgent, "Settings > Multi-Site", "/settings?tab=multisite&configure=x"),
                     (NoGatewaySsh, "Settings, under the Connection tab", "/settings#gateway-ssh"),
                     (AgentOffline, "Settings", (string?)null),
                     (NoAgent, "not present", "/settings"),
                 })
        {
            var linked = MessageLinker.Split(message, phrase, href);
            (linked.Before + linked.LinkText + linked.After).Should().Be(message);
        }
    }
}
