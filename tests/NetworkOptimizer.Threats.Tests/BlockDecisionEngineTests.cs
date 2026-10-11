using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class BlockDecisionEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BlockingSettings Enforce = new() { Mode = BlockingMode.Enforce };
    private static readonly BlockAllowlist NoAllowlist = new(null);
    private static readonly IReadOnlySet<string> NoActive = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, int> NoStrikes = new Dictionary<string, int>();

    private static List<ThreatBlock> Decide(
        IReadOnlyDictionary<string, int>? waf = null,
        IEnumerable<ThreatPattern>? patterns = null,
        BlockingSettings? settings = null,
        IReadOnlySet<string>? active = null,
        IReadOnlyDictionary<string, int>? strikes = null,
        BlockAllowlist? allowlist = null) =>
        BlockDecisionEngine.Decide(settings ?? Enforce, Now, waf ?? new Dictionary<string, int>(), patterns ?? [],
            active ?? NoActive, strikes ?? NoStrikes, allowlist ?? NoAllowlist);

    private static ThreatPattern Pattern(PatternType type, params string[] ips) =>
        new() { PatternType = type, SourceIpsJson = System.Text.Json.JsonSerializer.Serialize(ips) };

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 240)]
    [InlineData(2, 960)]
    [InlineData(3, 3840)]
    [InlineData(4, BlockingSettings.MaxBanMinutes)]
    [InlineData(20, BlockingSettings.MaxBanMinutes)]
    public void BanMinutes_MultipliesPerStrikeUpToSevenDays(int strikes, int expected) =>
        BlockDecisionEngine.BanMinutes(60, strikes).Should().Be(expected);

    [Fact]
    public void WafEvents_AtThreshold_Block()
    {
        var blocks = Decide(new Dictionary<string, int> { ["203.0.113.7"] = 5, ["198.51.100.4"] = 4 });

        var b = blocks.Should().ContainSingle().Subject;
        b.SourceIp.Should().Be("203.0.113.7");
        b.Trigger.Should().Be(BlockTrigger.WafRepeat);
        b.Reason.Should().Be("5 WAF events in 10 minutes");
        b.Enforced.Should().BeTrue();
        b.ExpiresAt.Should().Be(Now.AddMinutes(60));
    }

    [Fact]
    public void DryRun_RecordsButDoesNotEnforce()
    {
        var blocks = Decide(new Dictionary<string, int> { ["203.0.113.7"] = 9 }, settings: new BlockingSettings { Mode = BlockingMode.DryRun });

        blocks.Should().ContainSingle().Which.Enforced.Should().BeFalse();
    }

    [Fact]
    public void PriorStrikes_LengthenTheBan()
    {
        var blocks = Decide(new Dictionary<string, int> { ["203.0.113.7"] = 9 },
            strikes: new Dictionary<string, int> { ["203.0.113.7"] = 2 });

        var b = blocks.Should().ContainSingle().Subject;
        b.Strikes.Should().Be(2);
        b.ExpiresAt.Should().Be(Now.AddMinutes(960));
    }

    [Fact]
    public void AlreadyActive_IsNotBlockedAgain()
    {
        Decide(new Dictionary<string, int> { ["203.0.113.7"] = 9 }, active: new HashSet<string> { "203.0.113.7" })
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("192.168.1.70")]        // LAN
    [InlineData("10.0.0.5")]
    [InlineData("100.101.102.103")]     // Tailscale / CGNAT
    [InlineData("127.0.0.1")]
    [InlineData("fd00::1")]             // IPv6 ULA
    [InlineData("173.245.48.10")]       // Cloudflare edge
    [InlineData("2606:4700::6810:1")]   // Cloudflare edge, IPv6
    public void BuiltInAllowlist_IsNeverBlocked(string ip)
    {
        Decide(new Dictionary<string, int> { [ip] = 99 }).Should().BeEmpty();
    }

    [Fact]
    public void UserAllowlist_AndTrustedFilters_AreNeverBlocked()
    {
        var filters = new List<ThreatNoiseFilter>
        {
            new() { SourceIp = "203.0.113.20", Category = ThreatFilterCategory.TrustedUser, Enabled = true },
            new() { SourceIp = "203.0.113.21", Category = ThreatFilterCategory.Noise, Enabled = true },        // Noise hides, it does not trust
            new() { SourceIp = "203.0.113.22", Category = ThreatFilterCategory.Infrastructure, Enabled = false }, // disabled
        };
        var allowlist = new BlockAllowlist("198.51.100.0/24, 203.0.113.9", filters);

        var blocks = Decide(new Dictionary<string, int>
        {
            ["198.51.100.77"] = 9, ["203.0.113.9"] = 9, ["203.0.113.20"] = 9, ["203.0.113.21"] = 9, ["203.0.113.22"] = 9,
        }, allowlist: allowlist);

        blocks.Select(b => b.SourceIp).Should().BeEquivalentTo(["203.0.113.21", "203.0.113.22"]);
    }

    [Fact]
    public void Patterns_BlockTheirSources_ButNotDDoS()
    {
        var blocks = Decide(patterns:
        [
            Pattern(PatternType.ScanSweep, "203.0.113.1"),
            Pattern(PatternType.BruteForce, "203.0.113.2"),
            Pattern(PatternType.ExploitCampaign, "203.0.113.3", "203.0.113.4"),
            Pattern(PatternType.DDoS, "203.0.113.5"),
        ]);

        blocks.Select(b => b.SourceIp).Should().BeEquivalentTo(["203.0.113.1", "203.0.113.2", "203.0.113.3", "203.0.113.4"]);
        blocks.Should().OnlyContain(b => b.Trigger == BlockTrigger.Pattern);
        blocks.Single(b => b.SourceIp == "203.0.113.1").Reason.Should().Be("Scan sweep");
    }

    [Fact]
    public void Patterns_Off_OnlyWafCounts()
    {
        Decide(patterns: [Pattern(PatternType.ScanSweep, "203.0.113.1")], settings: Enforce with { UsePatterns = false })
            .Should().BeEmpty();
    }

    [Fact]
    public void OneSourceInBothWafAndPattern_IsBlockedOnce()
    {
        var blocks = Decide(new Dictionary<string, int> { ["203.0.113.1"] = 9 }, [Pattern(PatternType.ScanSweep, "203.0.113.1")]);

        blocks.Should().ContainSingle().Which.Trigger.Should().Be(BlockTrigger.WafRepeat);
    }

    [Fact]
    public void MalformedPatternSources_AreIgnored()
    {
        Decide(patterns: [new ThreatPattern { PatternType = PatternType.ScanSweep, SourceIpsJson = "not json" }])
            .Should().BeEmpty();
    }

    [Fact]
    public void DesiredSet_IsActiveEnforceOnly_NewestFirst_Capped()
    {
        var blocks = new List<ThreatBlock>
        {
            new() { SourceIp = "203.0.113.1", Enforced = true, CreatedAt = Now.AddMinutes(-30), ExpiresAt = Now.AddMinutes(30) },
            new() { SourceIp = "203.0.113.2", Enforced = true, CreatedAt = Now.AddMinutes(-10), ExpiresAt = Now.AddMinutes(50) },
            new() { SourceIp = "203.0.113.3", Enforced = false, CreatedAt = Now, ExpiresAt = Now.AddHours(1) },                 // Dry Run
            new() { SourceIp = "203.0.113.4", Enforced = true, CreatedAt = Now.AddHours(-2), ExpiresAt = Now.AddMinutes(-1) },  // expired
            new() { SourceIp = "203.0.113.5", Enforced = true, CreatedAt = Now, ExpiresAt = Now.AddHours(1), ReleasedAt = Now }, // released
            new() { SourceIp = "203.0.113.6", Enforced = true, CreatedAt = Now.AddMinutes(-5), ExpiresAt = Now.AddHours(1) },
        };

        BlockDecisionEngine.DesiredSet(blocks, Now, maxEntries: 2).Select(b => b.SourceIp)
            .Should().Equal("203.0.113.6", "203.0.113.2");
    }

    [Fact]
    public void Allowlist_Invalid_ReportsBadEntries()
    {
        BlockAllowlist.Invalid("203.0.113.1, 198.51.100.0/24\nnope 10.0.0.0/33 2001:db8::/48")
            .Should().Equal("nope", "10.0.0.0/33");
    }

    [Fact]
    public async Task Settings_DefaultAndClamp()
    {
        var store = new Dictionary<string, string?>
        {
            [BlockingSettingKeys.Mode] = "enforce",
            [BlockingSettingKeys.WafEventThreshold] = "0",
            [BlockingSettingKeys.BanMinutes] = "999999",
            [BlockingSettingKeys.TargetWaf] = "true",
            [BlockingSettingKeys.UsePatterns] = "garbage",
        };
        var s = await BlockingSettings.LoadAsync(k => Task.FromResult(store.GetValueOrDefault(k)));

        s.Mode.Should().Be(BlockingMode.Enforce);
        s.WafEventThreshold.Should().Be(BlockingSettings.MinThreshold);
        s.BanMinutes.Should().Be(BlockingSettings.MaxBanMinutes);
        s.TargetWaf.Should().BeTrue();
        s.TargetGateway.Should().BeFalse();
        s.UsePatterns.Should().BeTrue();
        s.WindowMinutes.Should().Be(10);
        s.MaxEntries.Should().Be(500);
    }
}
