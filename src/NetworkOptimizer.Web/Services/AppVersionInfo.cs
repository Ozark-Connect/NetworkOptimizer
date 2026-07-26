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
    public const string LatestAgentVersion = "2.2.0";

    /// <summary>Full informational version (e.g. "1.4.2" or "0.0.0-alpha.0.12").</summary>
    public static string Informational { get; }

    /// <summary>The X.Y.Z base version for a real release, or null for a source build.</summary>
    public static string? ReleaseVersion { get; }

    /// <summary>
    /// True when this is a source build rather than a published release. Covers both
    /// flavors MinVer produces: no reachable tag at all (0.0.0 base, e.g. a Docker
    /// build whose context has no .git) and commits past the last tag (prerelease
    /// height like "2.3.2-alpha.0.12", e.g. a native build from a git checkout).
    /// Build metadata is ignored: the SDK appends "+&lt;sha&gt;" to every build made from a
    /// git checkout, releases included, so only a prerelease component marks a build
    /// as unreleased. A build exactly on a release tag reports e.g. "2.3.1+abc123".
    /// </summary>
    public static bool IsSourceBuild =>
        ReleaseVersion is null || Informational.Split('+')[0] != ReleaseVersion;

    static AppVersionInfo()
    {
        var info = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Informational = info ?? "";
        var baseVersion = info is not null ? Regex.Match(info, @"^\d+\.\d+\.\d+").Value : "";
        ReleaseVersion = baseVersion.Length > 0 && !baseVersion.StartsWith("0.0.0") ? baseVersion : null;
    }
}
