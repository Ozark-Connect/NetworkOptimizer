using System.Reflection;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Single source of truth for how this build identifies itself. A released
/// build stamps a real MinVer version (e.g. 1.4.2); a plain source build has no
/// reachable tag, so MinVer falls back to a 0.0.0 base - which the UI surfaces
/// as "(source build)". Both the footer and the agent-setup instructions gate
/// on <see cref="IsSourceBuild"/>: released builds get the published Docker
/// one-liner, source builds get build-from-source directions.
/// </summary>
public static class AppVersionInfo
{
    /// <summary>
    /// The MINIMUM agent version carrying current agent behavior: the release in
    /// which the agent (or anything it links - AgentProtocol, Monitoring, Core)
    /// last changed in a way agents execute. Bumped MANUALLY as part of the
    /// release procedure; releases without agent-relevant changes leave it alone.
    /// Never set it past that release - the Multi-Site agent list shows an
    /// "Update agent" callout for enrolled agents reporting an older version
    /// than this, and over-bumping nags agents into pointless upgrades.
    /// </summary>
    public const string LatestAgentVersion = "2.6.0";

    /// <summary>Full informational version (e.g. "1.4.2" or "0.0.0-alpha.0.12").</summary>
    public static string Informational { get; }

    /// <summary>The X.Y.Z base version for a real release, or null for a source build.</summary>
    public static string? ReleaseVersion { get; }

    /// <summary>True when this is an untagged source build rather than a published release.</summary>
    public static bool IsSourceBuild => ReleaseVersion is null;

    static AppVersionInfo()
    {
        var info = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Informational = info ?? "";
        var baseVersion = info is not null ? Regex.Match(info, @"^\d+\.\d+\.\d+").Value : "";
        ReleaseVersion = baseVersion.Length > 0 && !baseVersion.StartsWith("0.0.0") ? baseVersion : null;
    }
}
