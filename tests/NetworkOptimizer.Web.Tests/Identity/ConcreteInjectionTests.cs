using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The gate engine's guarantee is that a mutating service is only reachable through its interface,
/// where the role gate, the audit envelope and the instance-authority check live. A component that
/// injects the CONCRETE class instead gets none of them - the DI proxy is never in the path.
///
/// Architecture test A2 already checks that every interface method carries a role gate. Nothing
/// checked that the interface was the only way in, which is how a Site Admin came to be able to
/// export every site's database, credentials and data-protection keys through Settings: the page
/// injected ConfigTransferService rather than IConfigTransferService.
///
/// The allowlist below is the set that existed when this test was written. It is DEBT, not
/// approval - each entry is a control whose gate is currently decorative. The point of the test is
/// that the list cannot grow without someone editing it in a reviewed diff.
/// </summary>
public class ConcreteInjectionTests
{
    /// <summary>
    /// Known concrete injections of a gated service, as "component:class". Do not add to this list
    /// to make a build pass - inject the interface instead. Removing an entry is always welcome.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // These five services expose a READ surface on the concrete class and gate only their writes,
        // because the reads draw maps, floor plans, AP markers, dashboards and the threat view - all
        // things a Viewer is meant to see in full. The component injects the concrete to read and the
        // gated *AdminService interface to write, so these entries are the read half and not debt.
        "ClientDashboard.razor:ApMapService",
        "ClientSpeedTest.razor:ApMapService",
        "Dashboard.razor:DashboardLayoutService",
        "Dashboard.razor:ThreatDashboardService",
        "FloorPlanEditor.razor:ApMapService",
        "FloorPlanEditor.razor:FloorPlanService",
        "FloorPlanEditor.razor:PlannedApService",
        "Monitoring.razor:DashboardLayoutService",
        "SpeedTest.razor:ApMapService",
        "ThreatDashboard.razor:ThreatDashboardService",
        "WiFiOptimizer.razor:ApMapService",
        "AirtimeFairness.razor:WiFiOptimizerService",
        "AlertsList.razor:AuditService",
        "ApLoadBalance.razor:WiFiOptimizerService",
        "Audit.razor:AuditService",
        "BandSteeringAnalysis.razor:WiFiOptimizerService",
        "ChannelAnalysis.razor:WiFiOptimizerService",
        "ClientTimeline.razor:WiFiOptimizerService",
        "ClientWanSpeedTest.razor:SystemSettingsService",
        "ConnectivityFlow.razor:WiFiOptimizerService",
        "EnvironmentalCorrelation.razor:WiFiOptimizerService",
        "FloorPlanEditor.razor:SystemSettingsService",
        "Metrics.razor:WiFiOptimizerService",
        "PowerCoverageAnalysis.razor:WiFiOptimizerService",
        "RoamingAnalytics.razor:WiFiOptimizerService",
        "Settings.razor:AuditService",
        "Settings.razor:SystemSettingsService",
        "SpectrumAnalysis.razor:WiFiOptimizerService",
        "SpeedTest.razor:SystemSettingsService",
        "SpeedTestMap.razor:SystemSettingsService",
        "WanSpeedTest.razor:SystemSettingsService",
        "WiFiOptimizer.razor:WiFiOptimizerService",
    };

    [Fact]
    public void No_new_component_injects_a_gated_service_concretely()
    {
        var webRoot = FindWebProjectRoot();
        var gated = GatedInterfaces(webRoot);
        var implementations = ImplementationsOf(gated, webRoot);

        var offenders = new List<string>();
        foreach (var razor in ComponentFiles(webRoot))
        {
            var text = File.ReadAllText(razor);
            var component = Path.GetFileName(razor);

            foreach (var (concrete, _) in implementations)
            {
                var injected =
                    Regex.IsMatch(text, $@"@inject\s+{Regex.Escape(concrete)}\s+\w+")
                    || Regex.IsMatch(text, $@"\[Inject\][^\n]*\bprivate\s+{Regex.Escape(concrete)}\b");

                if (injected && !Allowed.Contains($"{component}:{concrete}"))
                    offenders.Add($"{component} injects {concrete} instead of its gated interface");
            }
        }

        offenders.Should().BeEmpty(
            "injecting the concrete class bypasses the role gate, the instance-authority check and "
            + "the audit envelope - inject the interface, or add a reviewed allowlist entry saying why");
    }

    /// <summary>
    /// Every component file, markup AND code-behind. Scanning only *.razor missed a partial class in
    /// Component.razor.cs injecting the concrete implementation - the same bypass, in the file the
    /// [Inject] property is most likely to live in once a component grows past inline code.
    /// </summary>
    private static IEnumerable<string> ComponentFiles(string webRoot)
        => Directory.EnumerateFiles(
                Path.Combine(webRoot, "Components"), "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(webRoot, "Components"), "*.razor.cs", SearchOption.AllDirectories));

    [Fact]
    public void The_allowlist_has_no_stale_entries()
    {
        // A stale entry is a fix nobody noticed, and it keeps the debt list lying about its size.
        var webRoot = FindWebProjectRoot();
        var gated = GatedInterfaces(webRoot);
        var implementations = ImplementationsOf(gated, webRoot);

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var razor in ComponentFiles(webRoot))
        {
            var text = File.ReadAllText(razor);
            var component = Path.GetFileName(razor);
            foreach (var (concrete, _) in implementations)
            {
                if (Regex.IsMatch(text, $@"@inject\s+{Regex.Escape(concrete)}\s+\w+")
                    || Regex.IsMatch(text, $@"\[Inject\][^\n]*\bprivate\s+{Regex.Escape(concrete)}\b"))
                {
                    live.Add($"{component}:{concrete}");
                }
            }
        }

        Allowed.Except(live).Should().BeEmpty("these were fixed - remove them from the allowlist");
    }

    private static HashSet<string> GatedInterfaces(string webRoot)
    {
        var gated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(webRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(
                         File.ReadAllText(file), @"\[MutatingService[^\]]*\]\s*\r?\n\s*public interface (I\w+)"))
            {
                gated.Add(m.Groups[1].Value);
            }
        }
        return gated;
    }

    private static Dictionary<string, string> ImplementationsOf(HashSet<string> gated, string webRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(webRoot, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(
                         File.ReadAllText(file), @"public (?:sealed )?class (\w+)\s*:\s*([^\{]+)"))
            {
                foreach (var iface in gated)
                {
                    if (Regex.IsMatch(m.Groups[2].Value, $@"\b{Regex.Escape(iface)}\b"))
                        map[m.Groups[1].Value] = iface;
                }
            }
        }
        return map;
    }

    /// <summary>Walks up from the test assembly to the Web project, so this works from any runner.</summary>
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
