using System.Reflection;
using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// A registry that hands out one instance per site slug must be emptied when a site is removed,
/// or the instance - holding that site's console connection, InfluxDB client and database path -
/// is handed straight back to whatever is created under the same slug next. That is not a rare
/// case: a removed-and-re-added test site hits it every time, and the symptoms are remote from the
/// cause (Upstream path discovery reporting an empty device list, ISP Health throwing
/// ObjectDisposedException on a client somebody else disposed).
///
/// The sweep works off <see cref="ISiteScopedRegistry"/>, so a registry that does not implement it
/// is invisible to site removal. This test is the net for that.
/// </summary>
public class SiteScopedRegistryTests
{
    private static readonly Assembly WebAssembly = typeof(SiteManagementService).Assembly;

    /// <summary>
    /// Registries that key on something other than a site slug, so site removal has nothing to do
    /// with them. Each entry is a reviewed decision rather than an omission.
    /// </summary>
    private static readonly HashSet<string> NotPerSite = new()
    {
        // Keyed by agent id / tunnel, torn down by DropTunnels on removal.
        "AgentTunnelRegistry",
    };

    [Fact]
    public void EveryPerSiteRegistryCanBeSwept()
    {
        var perSite = WebAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Registry", StringComparison.Ordinal))
            .Where(t => !NotPerSite.Contains(t.Name))
            // The per-site shape: a GetFor(string) handing out one instance per slug.
            .Where(t => t.GetMethod("GetFor", BindingFlags.Public | BindingFlags.Instance, new[] { typeof(string) }) != null)
            .ToList();

        // Without this the test passes by matching nothing at all - a rename of the shape it looks
        // for would quietly retire the net rather than fail.
        perSite.Should().HaveCountGreaterThan(10, "the per-site registries should still be found by this shape");

        var offenders = perSite
            .Where(t => !typeof(ISiteScopedRegistry).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        offenders.Should().BeEmpty(
            "a registry with a per-slug GetFor must implement ISiteScopedRegistry and be registered "
            + "with AddSiteScopedRegistry, or removing a site leaves its instance behind for the next "
            + "site created under the same slug");
    }
}
