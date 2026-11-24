using System.Text.RegularExpressions;
using FluentAssertions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Times in text no one viewer owns (alerts, notes stored with a plan, audit reasons) name their zone. Alert
/// readers are often a channel or a mailing list, so a bare "8:47 AM" cannot be placed (2026-11-24: LAN Health's
/// Link Flapping said "Started Nov 24, 8:47 AM" on the site's Eastern clock to a reader on Central).
/// <see cref="CapturingBus"/> checks every alert a test publishes; these check the source and the guard itself.
/// </summary>
public class AlertTimeArchitectureTests
{
    // "u" inside an interpolated string ("2026-11-24 13:47:00Z"): reads like text, so it ends up in text. Log
    // templates are not interpolated, and "o" is left to wire values (JS interop, URLs, stored context).
    private static readonly Regex MachineFormat = new(@"\{[^{}""]*:u\}", RegexOptions.Compiled);

    // The viewer's clock in a file that publishes alerts: alert text has no viewer.
    private static readonly Regex ViewerClock = new(@"\bIDisplayTimeZone\b|\bDisplayTime\.(?:Zone|Format)\b", RegexOptions.Compiled);

    [Fact]
    public void No_interpolated_text_in_src_writes_a_machine_timestamp()
    {
        var src = FindSrcRoot();
        var offenders = new List<string>();
        foreach (var file in SourceFiles(src))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in MachineFormat.Matches(lines[i]))
                    if (IsInterpolated(lines[i], m.Index))
                        offenders.Add($"{Path.GetRelativePath(src, file)}:{i + 1}");
        }

        offenders.Should().BeEmpty(
            "text a person reads writes a time with TimeFormatHelper.FormatLabeledDateTime on the notification clock, "
            + "or as \"yyyy-MM-dd HH:mm UTC\" where there is no clock; \"u\" is a wire format");
    }

    [Fact]
    public void No_alert_publisher_writes_on_the_viewers_clock()
    {
        var src = FindSrcRoot();
        var offenders = SourceFiles(src)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f) is var t && t.Contains("new AlertEvent", StringComparison.Ordinal) && ViewerClock.IsMatch(t))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        offenders.Should().BeEmpty("alert text is written on the notification clock (INotificationTimeZoneResolver), never a viewer's");
    }

    [Theory]
    [InlineData("Started Nov 24, 8:47 AM, with none in the 15 days before.", "8:47 AM")]
    [InlineData("Last seen 2026-11-24 13:47:00Z.", "2026-11-24 13:47")]
    [InlineData("Scheduled for Nov 24, 14:42.", "14:42")]
    [InlineData("Started Nov 24, 8:47 AM EST, with none in the 15 days before.", null)]
    [InlineData("Scheduled for Nov 24, 14:42 UTC.", null)]
    [InlineData("Starting Mon Nov 24, 02:00 UTC+05:30, in 3 hours.", null)]
    [InlineData("Starting Sat Aug 15, 12:00 CDT (03:00 at-site), in about 1 day.", null)]
    [InlineData("Device aa:bb:cc:00:11:22 at 2001:db8::1 went offline.", null)]
    [InlineData("4 drops in 48h, about 5 seconds each.", null)]
    public void The_guard_finds_a_time_without_its_zone(string text, string? unlabeled) =>
        AlertTextGuard.FirstUnlabeledTime(text).Should().Be(unlabeled);

    [Fact]
    public async Task The_bus_refuses_an_alert_with_an_unlabeled_time()
    {
        var bus = new CapturingBus();
        var act = async () => await bus.PublishAsync(new AlertEvent { EventType = "test", Source = "test", Title = "T", Message = "Started 8:47 AM." });
        await act.Should().ThrowAsync<InvalidOperationException>();

        var ok = new AlertEvent { EventType = "test", Source = "test", Title = "T", Message = "Started 8:47 AM EST." };
        ok.Context["since"] = new DateTime(2026, 11, 24, 13, 47, 0, DateTimeKind.Utc).ToString("o");
        await bus.PublishAsync(ok);
        bus.Published.Should().ContainSingle();
    }

    [Fact]
    public void A_labeled_time_names_its_zone()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var at = new DateTime(2026, 11, 24, 13, 47, 0, DateTimeKind.Utc);

        var labeled = NetworkOptimizer.Web.Services.TimeFormatHelper.FormatLabeledDateTime(at, eastern, DisplayFormat.Fallback);
        labeled.Should().EndWith(" EST");
        AlertTextGuard.FirstUnlabeledTime($"Started {labeled}.").Should().BeNull();
    }

    /// <summary>Whether the match sits inside a $"..." literal opened earlier on the line.</summary>
    private static bool IsInterpolated(string line, int index)
    {
        var quote = line.LastIndexOf('"', index);
        while (quote > 0 && line[quote - 1] == '\\') quote = line.LastIndexOf('"', quote - 2);
        if (quote <= 0) return false;
        return line[quote - 1] == '$' || (quote > 1 && line[quote - 1] == '@' && line[quote - 2] == '$');
    }

    private static IEnumerable<string> SourceFiles(string src)
        => Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    /// <summary>Walks up from the test assembly to the repo's src folder, so this works from any runner.</summary>
    private static string FindSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "NetworkOptimizer.Web")))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/ from the test output directory.");
    }
}
