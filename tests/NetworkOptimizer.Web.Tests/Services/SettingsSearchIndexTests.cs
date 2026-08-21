using System.Text.RegularExpressions;
using FluentAssertions;
using NetworkOptimizer.Web.Services.Search;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// The Settings search index is hand-written metadata about markup that lives somewhere else, so the
/// two can drift apart silently: a renamed card id turns a search result into a jump that scrolls
/// nowhere, and nothing at compile time notices. These tests hold the index to the markup, and pin
/// the queries a user actually types to the card they mean.
/// </summary>
public class SettingsSearchIndexTests
{
    private static readonly AppSearchEntry[] Entries = SettingsSearchProvider.AllEntries.ToArray();

    // The tab ids Settings.razor accepts. An entry pointing at anything else lands on Connection.
    private static readonly HashSet<string> ValidTabs = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "monitoring", "speedtests", "security", "application", "identity", "multisite",
        "auditlog",
    };

    [Fact]
    public void Every_anchor_exists_in_the_markup()
    {
        var ids = ElementIdsInComponents();

        var missing = Entries
            .Where(e => e.Anchor is not null && !ids.Contains(e.Anchor))
            .Select(e => $"{e.Title} -> #{e.Anchor}")
            .ToList();

        missing.Should().BeEmpty("a search result whose anchor is gone scrolls nowhere");
    }

    [Fact]
    public void Every_entry_targets_a_real_tab()
    {
        foreach (var entry in Entries)
        {
            entry.Key.Should().NotBeNull();
            ValidTabs.Should().Contain(entry.Key!, $"'{entry.Title}' points at tab '{entry.Key}'");
            entry.Route.Should().StartWith($"/settings?tab={entry.Key}");
            entry.Area.Should().Be("Settings");
            entry.Section.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Anchors_are_unique()
    {
        var duplicates = Entries
            .Where(e => e.Anchor is not null)
            .GroupBy(e => e.Anchor, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.Should().BeEmpty();
    }

    [Fact]
    public void Every_card_on_the_page_is_indexed()
    {
        // The Settings tabs are drawn by putting settings-tab-<tab> on a card and hiding its
        // siblings, so this finds every card the page can show, whatever component it lives in.
        var cards = SettingsCardIds();
        var indexed = Entries.Select(e => e.Anchor).Where(a => a is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);

        cards.Except(indexed, StringComparer.OrdinalIgnoreCase).Should()
            .BeEmpty("a card the search cannot find is a section of Settings the search box misses");
    }

    [Theory]
    // Typed the title, or most of it.
    [InlineData("cable modem", "cable-modem")]
    [InlineData("starlink", "starlink")]
    [InlineData("guided tours", "guided-tours")]
    [InlineData("audit log", "auditlog")]
    [InlineData("multi site", "multi-site")]
    [InlineData("licensing", "licensing")]
    // Typed what the thing is, not what the card is called.
    [InlineData("docsis", "cable-modem")]
    [InlineData("influxdb", "monitoring")]
    [InlineData("snmp", "monitoring")]
    [InlineData("kiosk", "ui-display")]
    [InlineData("mapbox", "map")]
    [InlineData("backup", "data-management")]
    [InlineData("mfa", "identity-sign-in")]
    [InlineData("saml", "identity-sign-in")]
    [InlineData("geoip", "maxmind")]
    [InlineData("ntfy", "alert-channels")]
    [InlineData("iperf3 streams", "speed-test-settings")]
    // Bare "api key" is genuinely ambiguous - the console, CrowdSec and MaxMind all take one, and
    // all three belong in the list. Only the qualified form has one right answer.
    [InlineData("network api key", "console-connection")]
    [InlineData("ed25519", "ssh-key")]
    [InlineData("grace period", "security-audit")]
    [InlineData("notification channels", "alert-channels")]
    [InlineData("sso", "identity-sign-in")]
    [InlineData("enrollment token", "multi-site")]
    [InlineData("pre-release", "application-settings")]
    [InlineData("clear cache", "data-management")]
    // Typed the hardware they own rather than the card that monitors it.
    [InlineData("xfinity", "cable-modem")]
    [InlineData("quantum fiber", "ont-monitoring")]
    [InlineData("quectel", "cellular-modem")]
    [InlineData("speed test", "speed-test-settings")]
    // Typed it badly.
    [InlineData("cabl modm", "cable-modem")]
    [InlineData("adaptiv sqm", "sqm-monitor")]
    [InlineData("extrnal speed", "external-speedtest-settings")]
    public void The_best_result_is_the_card_the_user_meant(string query, string expectedTarget)
    {
        var top = Entries
            .Select(e => (Entry: e, Score: AppSearchService.ScoreEntry(e, query)))
            .Where(x => x.Score >= NetworkOptimizer.Core.Helpers.FuzzyMatch.MinimumUsefulScore)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        top.Entry.Should().NotBeNull($"'{query}' should match something");

        // Anchor identifies a card; an entry with no anchor is a whole tab, so its key names it.
        (top.Entry!.Anchor ?? top.Entry.Key).Should().Be(expectedTarget);
    }

    [Fact]
    public void The_Audit_Log_tab_is_its_own_destination()
    {
        // The tab holds the log and nothing else, so there is nothing to scroll to or ring inside
        // it. AnchorTabMap deliberately maps #audit-log to the tab with no element behind it.
        var auditLog = Entries.Single(e => e.Key == "auditlog");
        auditLog.Anchor.Should().BeNull();
    }

    [Fact]
    public void The_Roles_Reference_arrives_by_the_route_that_opens_it()
    {
        // The card is collapsed by default and already has a route that expands and rings it, used
        // by the Site role tooltips. Arriving by anchor would ring a closed header instead.
        var roles = Entries.Single(e => e.Title == "Roles Reference");
        roles.Route.Should().Be("/settings?tab=identity&roles=1");
    }

    [Fact]
    public void The_combined_search_text_carries_every_field_and_is_stable_when_cached()
    {
        var entry = Entries.Single(e => e.Anchor == "cable-modem");

        var first = entry.SearchText;
        first.Should().Contain(entry.Title).And.Contain(entry.Section!).And.Contain(entry.Area);
        foreach (var word in entry.Aliases.Concat(entry.Keywords))
            first.Should().Contain(word);

        entry.SearchText.Should().BeSameAs(first, "it is built once and reused");
    }

    [Fact]
    public void Nonsense_matches_nothing()
    {
        var hits = Entries
            .Select(e => AppSearchService.ScoreEntry(e, "zzqxwv"))
            .Where(s => s >= NetworkOptimizer.Core.Helpers.FuzzyMatch.MinimumUsefulScore);

        hits.Should().BeEmpty();
    }

    private static HashSet<string> ElementIdsInComponents()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ComponentFiles())
        foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\bid=""([A-Za-z0-9_-]+)"""))
            ids.Add(match.Groups[1].Value);

        return ids;
    }

    /// <summary>Ids of every card that renders inside a Settings tab, wherever it is declared.</summary>
    private static HashSet<string> SettingsCardIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ComponentFiles())
        foreach (Match match in Regex.Matches(
                     File.ReadAllText(file),
                     @"class=""card settings-tab-[a-z]+""\s+id=""([A-Za-z0-9_-]+)"""))
            ids.Add(match.Groups[1].Value);

        return ids;
    }

    private static IEnumerable<string> ComponentFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(FindWebProjectRoot(), "Components"), "*.razor", SearchOption.AllDirectories);

    private static string FindWebProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "NetworkOptimizer.Web");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/NetworkOptimizer.Web from the test output directory.");
    }
}
