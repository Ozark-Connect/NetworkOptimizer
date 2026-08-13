using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// The PDF export renders a scored report; it computes nothing, so what's worth proving is
/// that every section survives QuestPDF's layout pass for the report shapes the tab can
/// produce - a full one, and the sparse one a freshly set up site has.
/// </summary>
public class IspHealthPdfGeneratorTests
{
    private static readonly AccessProfile Gpon = IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!;
    private static readonly DateTime WindowEnd = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = WindowEnd.AddHours(-48);

    private static IspHealthReport MinimalReport() => new()
    {
        OverallScore = 88,
        ComputedAt = WindowEnd,
        WindowStart = WindowStart,
        WindowEnd = WindowEnd,
        Profile = Gpon,
        AccessTechnology = AccessTechnology.Gpon,
        AccessDimension = new IspScoreDimension { Name = "Access Layer", Score = 90, Weight = 0.5 },
        TransitDimension = new IspScoreDimension { Name = "Transit", Score = 85, Weight = 0.25 },
        IspAsnDimension = new IspScoreDimension { Name = "ISP Network", Score = 87, Weight = 0.25 }
    };

    private static IspHealthReport FullReport()
    {
        var report = new IspHealthReport
        {
            OverallScore = 74,
            ComputedAt = WindowEnd,
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            Profile = Gpon,
            AccessTechnology = AccessTechnology.Gpon,
            AccessDimension = new IspScoreDimension
            {
                Name = "Access Layer",
                Score = 71,
                Weight = 0.5,
                Factors =
                {
                    new IspScoreFactor
                    {
                        Name = "Idle Latency", Score = 95, Weight = 0.3,
                        ValueText = "1.8 ms", Description = "Idle latency to the first ISP hop."
                    },
                    new IspScoreFactor
                    {
                        Name = "Speed vs Plan", Score = 62, Weight = 0.2,
                        ValueText = "780 / 480 Mbps", Description = "Measured throughput against your plan."
                    },
                    new IspScoreFactor { Name = "Packet Loss", Score = null, Weight = 0.2 }
                }
            },
            TransitDimension = new IspScoreDimension
            {
                Name = "Transit",
                Score = 80,
                Weight = 0.25,
                Factors =
                {
                    new IspScoreFactor
                    {
                        Name = "Example Transit", Score = 80, Weight = 1.0, ValueText = "18.20 ms",
                        InvolvementTooltip = "Carries 6 of 8 internet targets (forward path), 100% weight"
                    },
                    new IspScoreFactor
                    {
                        Name = "Example Side Path", Score = 58, Weight = 0.25, ValueText = "31.40 ms",
                        InvolvementTooltip = "Off the forward path; held at 25% (likely the return path from popular services)",
                        LowReachScoreCaveat = "Lightly weighted - nothing we are monitoring routes through this network; a low score is likely just ICMP deprioritization."
                    }
                }
            },
            IspAsnDimension = new IspScoreDimension { Name = "ISP Network", Score = 77, Weight = 0.25 },
            HasExpectedSpeeds = true,
            HasUpstreamTraceMap = true,
            ExpectedDownloadMbps = 1000,
            ExpectedUploadMbps = 500,
            ExpectedSpeedSource = "UniFi Network expected ISP speeds",
            MeasuredDownloadMbps = 780,
            MeasuredUploadMbps = 480,
            TypicalDownloadMbps = 742.5,
            TypicalUploadMbps = 465,
            SpeedTestTime = WindowEnd.AddHours(-3)
        };

        report.IspTargets.Add(new IspTargetHealth
        {
            TargetId = "isp-1", Name = "ISP hop 1", Address = "198.51.100.1 - core1.example.net",
            RttMs = 1.8, ScoredJitterMs = 0.2, LossPct = 0, OverallScore = 94, IsGradedHop = true,
            LatencyStabilityScore = 96, CongestionScore = 100
        });
        report.IspTargets.Add(new IspTargetHealth
        {
            TargetId = "isp-2", Name = "ISP hop 2", RttMs = 4.1, ScoredJitterMs = 1.4,
            RawJitterMs = 3.2, JitterAssimilated = true, LossPct = 0.02, OverallScore = 81,
            LatencyStabilityScore = 88, CongestionScore = 74, CongestionEventCount = 1,
            NotOnTracedPath = true
        });

        report.IspAsns.Add(new IspAsnHealth
        {
            AsnNumber = 64500, AsnName = "Example Access", MeanRttMs = 2.9,
            ScoredJitterMs = 0.4, RawJitterMs = 1.9, JitterAssimilated = true,
            LossPct = 0, OverallScore = 88, LatencyStabilityScore = 92, CongestionScore = 100
        });
        report.TransitAsns.Add(new IspAsnHealth
        {
            AsnNumber = 64501, AsnName = "Example Transit", MeanRttMs = 18.2,
            ScoredJitterMs = 1.1, LossPct = 0.05, OverallScore = 79, CongestionEventCount = 1,
            LatencyStabilityScore = 90, CongestionScore = 68,
            ShowInvolvement = true, InvolvementReach = 6, InvolvementHostTotal = 8, InvolvementWeight = 1.0
        });
        // Off the forward path: the reach-0 case, which carries both the involvement note and
        // the ICMP-deprioritization caveat.
        report.TransitAsns.Add(new IspAsnHealth
        {
            AsnNumber = 64502, AsnName = "Example Side Path", MeanRttMs = 31.4,
            ScoredJitterMs = 2.6, LossPct = 0.4, OverallScore = 58,
            LatencyStabilityScore = 61, CongestionScore = 55,
            ShowInvolvement = true, InvolvementReach = 0, InvolvementHostTotal = 8, InvolvementWeight = 0.25
        });
        // A direct-peering entry: negative ASN, which the role label special-cases.
        report.TransitAsns.Add(new IspAsnHealth
        {
            AsnNumber = -1, AsnName = "IX Peering", MeanRttMs = 9.4,
            ScoredJitterMs = 0.6, LossPct = 0, OverallScore = 91
        });

        report.Issues.Add(new IspHealthIssue
        {
            Severity = IspIssueSeverity.Warning,
            Title = "Throughput below plan",
            Description = "Best measured download is 78% of the 1000 Mbps plan.",
            Recommendation = "Re-run a WAN speed test while the line is idle."
        });
        report.Issues.Add(new IspHealthIssue
        {
            Severity = IspIssueSeverity.Critical,
            Title = "Internet outage",
            Description = "The internet was unreachable for 12 minutes."
        });

        report.CongestionEvents.Add(new CongestionEvent
        {
            Start = WindowEnd.AddHours(-20),
            End = WindowEnd.AddHours(-19),
            AsnNumbers = { 64501 },
            AsnNames = { "Example Transit" },
            BaselineRttMs = 18.2,
            PeakRttMs = 46.7,
            BaselineJitterMs = 1.1,
            PeakJitterMs = 8.4,
            BottleneckLabel = "Example Transit hop 2",
            BottleneckHopIp = "198.51.100.9",
            Disposition = CongestionDisposition.Confirmed,
            Scope = CongestionScope.Hop
        });
        report.PathShifts.Add(new PathShiftEvent
        {
            Time = WindowEnd.AddHours(-30),
            AsnNumber = 64501,
            AsnName = "Example Transit",
            BeforeMedianMs = 18.2,
            AfterMedianMs = 24.6,
            CorrelatedTargetCount = 3
        });
        report.PathShifts.Add(new PathShiftEvent
        {
            Time = WindowEnd.AddHours(-8),
            AsnName = "Example Transit",
            IsUnreachable = true,
            UnreachableEnd = WindowEnd.AddHours(-7.8)
        });

        report.Outages.Add(new OutageEvent
        {
            Start = WindowEnd.AddHours(-26),
            End = WindowEnd.AddHours(-26).AddMinutes(12),
            Scope = OutageScope.Upstream,
            LastReachableHop = "ISP hop 1",
            PeakLossPct = 100,
            DegradedTargetCount = 5,
            PathTargetCount = 6,
            ScorePenaltyPoints = 4,
            Tiers =
            {
                new OutageTierState { Name = "ISP hop 1", Depth = 0, PeakLossPct = 0, WentDark = false },
                new OutageTierState
                {
                    Name = "Example Transit", Depth = 1, PeakLossPct = 100, WentDark = true,
                    RecoveredAt = WindowEnd.AddHours(-26).AddMinutes(11)
                }
            }
        });

        return report;
    }

    [Fact]
    public void GenerateReportBytes_RendersAFullReport()
    {
        var pdf = new IspHealthPdfGenerator().GenerateReportBytes(FullReport(), "Test Site");

        pdf.Should().NotBeNull();
        pdf.Length.Should().BeGreaterThan(1000);
        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void GenerateReportBytes_RendersASparseReport()
    {
        // A site that just finished setup: scores but no hops, ASNs, events, or plan speeds.
        var pdf = new IspHealthPdfGenerator().GenerateReportBytes(MinimalReport());

        pdf.Length.Should().BeGreaterThan(1000);
        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void GenerateReportBytes_WorksWithoutASiteName()
    {
        var pdf = new IspHealthPdfGenerator().GenerateReportBytes(FullReport());

        pdf.Length.Should().BeGreaterThan(1000);
    }

    [Theory]
    [InlineData(AccessTechnology.Gpon, false, "GPON")]
    [InlineData(AccessTechnology.Gpon, true, "GPON (PPPoE)")]
    [InlineData(AccessTechnology.XgsPon, true, "XGS-PON (PPPoE)")]
    [InlineData(AccessTechnology.DirectEthernet, true, "Active Ethernet (PPPoE)")]
    // A medium that already carries a qualifier folds the session into it rather than
    // stacking a second bracket.
    [InlineData(AccessTechnology.Dsl, false, "DSL (ADSL/VDSL)")]
    [InlineData(AccessTechnology.Dsl, true, "DSL (ADSL/VDSL, PPPoE)")]
    [InlineData(AccessTechnology.Docsis, false, "DOCSIS (Cable)")]
    [InlineData(AccessTechnology.Satellite, false, "Satellite (LEO)")]
    public void ScoredAsLabel_NamesTheMediumAndAnyDetectedSession(
        AccessTechnology tech, bool pppoe, string expected)
    {
        var report = MinimalReport();
        report.AccessTechnology = tech;
        report.PppoeSession = pppoe;

        IspHealthPresentation.ScoredAsLabel(report).Should().Be(expected);
    }

    [Theory]
    [InlineData(AccessTechnology.PppoE, "PPPoE")]
    [InlineData(AccessTechnology.Other, "Other")]
    [InlineData(AccessTechnology.Unknown, "Not detected")]
    public void ScoredAsLabel_NeverPrintsRawEnumCasing(AccessTechnology tech, string expected)
    {
        // The old catch-all fell through to tech.ToString(), so a legacy PppoE site's exported
        // PDF read "PppoE" - C# casing in a document that gets sent to an ISP.
        var report = MinimalReport();
        report.AccessTechnology = tech;

        IspHealthPresentation.ScoredAsLabel(report).Should().Be(expected);
    }

    [Fact]
    public void ScoredAsLabel_NamesTheTechnologyOnlyOnce()
    {
        // Regression: the line used to be "{Profile.DisplayName} ({FormatTechName})", which
        // rendered "DOCSIS (DOCSIS (Cable))".
        foreach (var tech in Enum.GetValues<AccessTechnology>())
        {
            var report = MinimalReport();
            report.AccessTechnology = tech;

            var label = IspHealthPresentation.ScoredAsLabel(report);
            label.Count(c => c == '(').Should().BeLessThanOrEqualTo(1, $"{tech} should not nest qualifiers");
        }
    }

    [Fact]
    public void ScoredWanLabel_NamesTheWanWithItsGroupAndInterface()
    {
        var report = MinimalReport();
        report.WanName = "Example Fiber";
        report.WanNetworkGroup = "WAN2";
        report.WanInterface = "eth5";

        // The group is what tells two WANs apart once more than one is scored.
        IspHealthPresentation.ScoredWanLabel(report).Should().Be("Example Fiber (WAN2, eth5)");
    }

    [Fact]
    public void ScoredWanLabel_FallsBackToTheGroupWhenTheWanIsUnnamed()
    {
        var report = MinimalReport();
        report.WanNetworkGroup = "WAN";
        report.WanInterface = "eth4";

        // The console calls the first WAN "WAN"; every surface displays it as WAN1.
        IspHealthPresentation.ScoredWanLabel(report).Should().Be("WAN1 (eth4)");
    }

    [Fact]
    public void ScoredWanLabel_DisplaysTheFirstWanAsWan1()
    {
        var report = MinimalReport();
        report.WanName = "Fiber Supplement";
        report.WanNetworkGroup = "WAN";
        report.WanInterface = "eth6";

        IspHealthPresentation.ScoredWanLabel(report).Should().Be("Fiber Supplement (WAN1, eth6)");
    }

    [Fact]
    public void ScoredWanLabel_IsNullWhenTheConsoleReportedNoWan()
    {
        IspHealthPresentation.ScoredWanLabel(MinimalReport()).Should().BeNull();
    }

    [Fact]
    public void AccessIspLabel_NamesTheAccessAsn()
    {
        IspHealthPresentation.AccessIspLabel(FullReport()).Should().Be("Example Access (AS64500)");
    }

    [Fact]
    public void AccessIspLabel_IsNullWhenDiscoveryFoundNoAccessAsn()
    {
        // A synthetic (negative) ASN names no operator, so it must not become the header ISP.
        var report = MinimalReport();
        report.IspAsns.Add(new IspAsnHealth { AsnNumber = -1, AsnName = "IX Peering" });

        IspHealthPresentation.AccessIspLabel(report).Should().BeNull();
    }

    [Fact]
    public void GenerateReportBytes_RendersWithTheWanAndIspNamed()
    {
        var report = FullReport();
        report.WanName = "Example Fiber";
        report.WanNetworkGroup = "WAN";
        report.WanInterface = "eth4";

        var pdf = new IspHealthPdfGenerator().GenerateReportBytes(report, "Test Site");

        pdf.Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void EventTimeline_DescribesEveryEventTheReportCarries()
    {
        // The PDF renders this feed verbatim, so a report with three events must produce
        // three lines - the same ones the tab lists.
        var entries = IspHealthPresentation.EventTimeline(FullReport()).ToList();

        entries.Should().HaveCount(3);
        entries.Should().ContainSingle(e => e.Badge == "Congestion");
        entries.Should().ContainSingle(e => e.Badge == "Path shift");
        entries.Should().ContainSingle(e => e.Badge == "Path change");
        entries.Should().BeInAscendingOrder(e => e.Time);
    }
}
