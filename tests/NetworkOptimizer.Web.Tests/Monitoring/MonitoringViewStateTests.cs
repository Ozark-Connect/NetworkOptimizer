using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;
using static NetworkOptimizer.Web.Services.Monitoring.MonitoringLinks;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The view a link or a jump lands Monitoring on. Cases cite the rule (R#) or history scenario (S#) or triage item (T#)
/// they protect: research/monitoring-view-state/plan.md and its inventories.
/// </summary>
public class MonitoringViewStateTests
{
    private static readonly IReadOnlyList<SiteWan> OneWan = [new("wan", true)];
    private static readonly IReadOnlyList<SiteWan> ThreeWans = [new("wan", true), new("wan2", false), new("wan3", false)];
    private static readonly IReadOnlyList<SiteWan> NoWans = [];

    private static AnalysisScope Saved(AnalysisScopeKind kind, params string[] keys) => new(kind, keys);

    private static MonitoringLink Link(string? category = null, string? wan = null, string? investigate = null,
        string? target = null) => new(category, target, At: "1700000000000", Span: null, investigate, wan);

    // ── LAN links (R2, R13, T2, T6) ──

    [Theory]
    [InlineData("lan")]
    [InlineData("all")]
    [InlineData(null)]
    public void LanLink_LandsOnTheLanScope_WhateverWanToken_OnEverySiteShape(string? wan)
    {
        foreach (var wans in new[] { OneWan, ThreeWans, NoWans })
        {
            var scope = MonitoringViewState.LinkScope(Link(FabricCategory, wan), wans, Saved(AnalysisScopeKind.Wans, "wan2"));
            scope!.Kind.Should().Be(AnalysisScopeKind.Lan);
            // The WAN set stays as saved, unlit, so going back to WANs restores it (R21, R28).
            scope.WanKeys.Should().Equal("wan2");
        }
    }

    [Fact]
    public void LanLink_NeedsNoWanList_SoItLandsBeforeTheWansLoad()
    {
        // T2: an agent site right after a reconnect has no WAN list yet; the LAN landing must not wait on it.
        MonitoringViewState.LinkNeedsWans(Link(FabricCategory, "all")).Should().BeFalse();
        MonitoringViewState.LinkNeedsWans(Link(wan: "lan")).Should().BeFalse();
        MonitoringViewState.LinkNeedsWans(Link(AccessIspCategory, "wan2")).Should().BeTrue();
    }

    [Fact]
    public void LanTokenAlone_LandsOnLan_SitesCardLanIssue()
    {
        // R13: the Sites card's LAN issue sends wan=lan with no category.
        MonitoringViewState.LinkScope(Link(wan: "lan"), ThreeWans, Saved(AnalysisScopeKind.All, "wan"))!
            .Kind.Should().Be(AnalysisScopeKind.Lan);
    }

    // ── WAN links (R4, R22, R23) ──

    [Fact]
    public void WanLink_LandsOnTheNamedWans_MatchedByIndex()
    {
        var scope = MonitoringViewState.LinkScope(Link(TransitCategory, "WAN1,wan3"), ThreeWans, Saved(AnalysisScopeKind.Lan));
        scope!.Kind.Should().Be(AnalysisScopeKind.Wans);
        scope.WanKeys.Should().Equal("wan", "wan3");
    }

    [Fact]
    public void WanLink_NamingEveryWanOfAMultiWanSite_IsAll()
    {
        MonitoringViewState.LinkScope(Link(AccessIspCategory, "wan,wan2,wan3"), ThreeWans, Saved(AnalysisScopeKind.Lan))!
            .Kind.Should().Be(AnalysisScopeKind.All);
    }

    [Fact]
    public void AllToken_IsAll_EvenOnASingleWanSite()
    {
        // R22: only All keeps the LAN categories next to the WAN ones on a single-WAN site.
        MonitoringViewState.LinkScope(Link(AccessIspCategory, "all"), OneWan, Saved(AnalysisScopeKind.Lan))!
            .Kind.Should().Be(AnalysisScopeKind.All);
    }

    [Fact]
    public void SingleWanLinkNamingItsWan_IsThatWan_NotAll()
    {
        var scope = MonitoringViewState.LinkScope(Link(AccessIspCategory, "wan"), OneWan, Saved(AnalysisScopeKind.Lan));
        scope!.Kind.Should().Be(AnalysisScopeKind.Wans);
        scope.WanKeys.Should().Equal("wan");
    }

    [Fact]
    public void PrimaryToken_NamesTheRole()
    {
        IReadOnlyList<SiteWan> wans = [new("wan", false), new("wan2", true)];
        MonitoringViewState.LinkScope(Link(AccessIspCategory, "primary"), wans, Saved(AnalysisScopeKind.Lan))!
            .WanKeys.Should().Equal("wan2");
    }

    [Fact]
    public void LinkNamingAWanTheSiteLacks_DoesNotNarrow()
    {
        MonitoringViewState.LinkScope(Link(AccessIspCategory, "wan7"), ThreeWans, Saved(AnalysisScopeKind.Lan)).Should().BeNull();
    }

    [Fact]
    public void WanCategoryWithNoWanNamed_OnASavedLanScope_LandsOnTheSavedWans()
    {
        // An alert for an unpinned ISP target names no WAN; the LAN scope cannot show ISP, so it lands on the saved
        // WANs (unlit under LAN), else the primary.
        MonitoringViewState.LinkScope(Link(AccessIspCategory), ThreeWans, Saved(AnalysisScopeKind.Lan, "wan3"))!
            .Should().BeEquivalentTo(new AnalysisScope(AnalysisScopeKind.Wans, ["wan3"]));
        MonitoringViewState.LinkScope(Link(AccessIspCategory), ThreeWans, Saved(AnalysisScopeKind.Lan))!
            .WanKeys.Should().Equal("wan");
    }

    [Fact]
    public void WanCategoryWithNoWanNamed_OnASavedWanOrAllScope_KeepsIt()
    {
        MonitoringViewState.LinkScope(Link(AccessIspCategory), ThreeWans, Saved(AnalysisScopeKind.All, "wan")).Should().BeNull();
        MonitoringViewState.LinkScope(Link(TransitCategory), ThreeWans, Saved(AnalysisScopeKind.Wans, "wan2")).Should().BeNull();
    }

    // ── Custom (R3, R10) ──

    [Fact]
    public void CustomLink_LandsWhereItsWanTokenSays_ElseTheSavedScope()
    {
        MonitoringViewState.LinkScope(Link(CustomCategory, "all"), ThreeWans, Saved(AnalysisScopeKind.Lan))!
            .Kind.Should().Be(AnalysisScopeKind.All);
        MonitoringViewState.LinkScope(Link(CustomCategory, "wan2"), ThreeWans, Saved(AnalysisScopeKind.Lan))!
            .WanKeys.Should().Equal("wan2");
        MonitoringViewState.LinkScope(Link(CustomCategory), ThreeWans, Saved(AnalysisScopeKind.Lan)).Should().BeNull();
    }

    // ── What does not narrow (R15) ──

    [Fact]
    public void DiscoverLinkWan_DoesNotNarrow()
    {
        MonitoringViewState.LinkScope(Link(wan: "wan2"), ThreeWans, Saved(AnalysisScopeKind.Lan)).Should().BeNull();
    }

    [Fact]
    public void InvestigateLink_NarrowsToItsWan()
    {
        MonitoringViewState.LinkScope(Link(wan: "wan2", investigate: "packet-loss"), ThreeWans, Saved(AnalysisScopeKind.Lan))!
            .WanKeys.Should().Equal("wan2");
    }

    // ── Landing category (R24, R39) ──

    [Theory]
    [InlineData(FabricCategory, AnalysisScopeKind.Lan, false, FabricCategory)]
    [InlineData(FabricCategory, AnalysisScopeKind.All, true, FabricCategory)]
    [InlineData(FabricCategory, AnalysisScopeKind.All, false, AccessIspCategory)]
    [InlineData(TransitCategory, AnalysisScopeKind.Wans, false, TransitCategory)]
    [InlineData(TransitCategory, AnalysisScopeKind.Lan, false, FabricCategory)]
    [InlineData(CustomCategory, AnalysisScopeKind.Lan, false, CustomCategory)]
    [InlineData(null, AnalysisScopeKind.Wans, false, AccessIspCategory)]
    [InlineData(null, AnalysisScopeKind.All, true, FabricCategory)]
    public void LandingCategory_IsTheAskedOneWhenTheScopeShowsIt(string? asked, AnalysisScopeKind kind, bool lanTargets, string expected)
    {
        MonitoringViewState.LandingCategory(asked, kind, lanTargets).Should().Be(expected);
    }

    [Fact]
    public void CategoriesPerScope_MatchTheChart()
    {
        MonitoringViewState.CategoriesFor(AnalysisScopeKind.Lan).Should().Equal(FabricCategory, CustomCategory);
        MonitoringViewState.CategoriesFor(AnalysisScopeKind.Wans).Should().NotContain(FabricCategory);
        MonitoringViewState.CategoriesFor(AnalysisScopeKind.All).Should().HaveCount(5);
    }

    // ── Watch in Live View (T1) ──

    [Fact]
    public void WatchInLiveView_FromTheLanScope_CarriesNoWan_SoLiveViewKeepsItsOwn()
    {
        MonitoringViewState.LiveJumpWan(Saved(AnalysisScopeKind.Lan, "wan2"), multiWanUi: true).Should().BeNull();
    }

    [Fact]
    public void WatchInLiveView_CarriesTheWansOnScreen_OrAll()
    {
        MonitoringViewState.LiveJumpWan(Saved(AnalysisScopeKind.Wans, "wan", "wan3"), true).Should().Be("wan,wan3");
        MonitoringViewState.LiveJumpWan(Saved(AnalysisScopeKind.All, "wan", "wan2", "wan3"), true).Should().Be("all");
        MonitoringViewState.LiveJumpWan(Saved(AnalysisScopeKind.Wans, "wan"), multiWanUi: false).Should().BeNull();
    }
}
