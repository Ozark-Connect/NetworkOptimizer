using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// A component that opens a DbContext and saves is writing outside the gate engine entirely: no role
/// check, no audit envelope, no site-authority check, and nothing for architecture test A2 to see,
/// because A2 examines mutating SERVICES and a page is not one. That is how the WAN Steering rules
/// and the per-site tunnel settings both came to be editable by anyone who could reach the page.
///
/// The allowlist below is what existed when this test was written. It is DEBT, not approval: each
/// entry is a write whose only protection is whichever wrapper happens to surround the control. The
/// point of the test is that the list cannot grow without someone editing it in a reviewed diff.
/// </summary>
public class ComponentDatabaseWriteTests
{
    /// <summary>
    /// Components known to save directly. Do not add to this list to make a build pass - move the
    /// write into a [MutatingService] with a role gate instead. Removing an entry is always welcome.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "FlakyTargetsCard.razor",
        "InfluxSetupWizard.razor",
        "LatencyTargetsCard.razor",
        "Monitoring.razor",
        "Settings.razor",
        "SfpModulesCard.razor",
        "SnmpDeviceStatusCard.razor",
        "UpnpInspector.razor",
        "WanContextsCard.razor",
    };

    [Fact]
    public void No_new_component_writes_to_a_database_directly()
    {
        var componentsRoot = Path.Combine(FindWebProjectRoot(), "Components");

        var offenders = Directory
            .EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .Where(razor => WritesToTheDatabase(File.ReadAllText(razor)))
            .Select(Path.GetFileName)
            .Where(name => name is not null && !Allowed.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a component that saves its own changes bypasses the role gate and the audit trail - "
            + "put the write behind a [MutatingService] instead");
    }

    [Fact]
    public void The_allowlist_has_no_stale_entries()
    {
        var componentsRoot = Path.Combine(FindWebProjectRoot(), "Components");

        var writing = Directory
            .EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            .Where(razor => WritesToTheDatabase(File.ReadAllText(razor)))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        var stale = Allowed.Where(entry => !writing.Contains(entry)).OrderBy(e => e, StringComparer.Ordinal);

        stale.Should().BeEmpty("these no longer write directly - drop them from the list so it keeps shrinking");
    }

    /// <summary>
    /// A component reaching the database directly, by any of the ways EF offers.
    ///
    /// Matching only SaveChangesAsync left the guard blind to ExecuteUpdateAsync and
    /// ExecuteDeleteAsync, which bypass the change tracker entirely and never call it - so a new
    /// component could write straight to the database and this list would go on claiming it could
    /// only shrink. Those two are in use elsewhere in this very suite, so it was not a theoretical
    /// gap.
    /// </summary>
    private static bool WritesToTheDatabase(string source)
        => source.Contains("SaveChangesAsync", StringComparison.Ordinal)
            || source.Contains("SaveChanges(", StringComparison.Ordinal)
            || source.Contains("ExecuteUpdateAsync", StringComparison.Ordinal)
            || source.Contains("ExecuteDeleteAsync", StringComparison.Ordinal);

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

        throw new DirectoryNotFoundException("Could not locate src/NetworkOptimizer.Web from the test output.");
    }
}
