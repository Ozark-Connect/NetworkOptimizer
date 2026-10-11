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
    /// The MINIMUM agent version every agent should run: the release whose agent
    /// change is worth nagging the whole fleet about. Bumped MANUALLY as part of
    /// the release procedure, and only for such a change. Never set it past that
    /// release - the Multi-Site agent list shows an "Agent needs update" warning
    /// for enrolled agents reporting an older version than this, and
    /// over-bumping nags agents into pointless upgrades. A prerelease tag counts
    /// as a release here: IsOlderThan ranks a release above its own prereleases,
    /// so "2.8.0" would nag every preview8 agent running byte-identical code -
    /// while a stable "2.8.0" agent still satisfies this gate.
    /// </summary>
    public const string RequiredAgentVersion = "2.8.0-preview8";

    /// <summary>
    /// The newest agent release: the last release in which the agent (or anything
    /// it links - AgentProtocol, Monitoring, Core) changed in a way agents execute.
    /// Bumped MANUALLY with every such release. An agent older than this but not
    /// older than <see cref="RequiredAgentVersion"/> is offered the upgrade
    /// without a warning, which is how an optional agent feature (UniFi Cable
    /// Internet capture) reaches the sites that want it without nagging the rest.
    /// Never lower than <see cref="RequiredAgentVersion"/>.
    /// </summary>
    public const string LatestAgentVersion = "2.9.3-preview2";

    /// <summary>Full informational version (e.g. "1.4.2" or "0.0.0-alpha.0.12").</summary>
    public static string Informational { get; }

    /// <summary>The X.Y.Z base version for a real release, or null for a source build.</summary>
    public static string? ReleaseVersion { get; }

    /// <summary>True when this is an untagged source build rather than a published release.</summary>
    public static bool IsSourceBuild => ReleaseVersion is null;

    /// <summary>
    /// The release agents should install from (e.g. "v2.9.1-preview1"), passed to the installers as
    /// --release; null leaves them on their default, the latest stable release. Set only for a
    /// prerelease build: GitHub's "latest" skips prereleases, so a preview server would otherwise
    /// hand out a stable agent. A stable server is the latest release (or older, where the latest
    /// agent is still right), and a source build has no release to pin to.
    /// </summary>
    public static string? AgentReleaseTag { get; }

    static AppVersionInfo()
    {
        var info = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Informational = info ?? "";
        var baseVersion = info is not null ? Regex.Match(info, @"^\d+\.\d+\.\d+").Value : "";
        ReleaseVersion = baseVersion.Length > 0 && !baseVersion.StartsWith("0.0.0") ? baseVersion : null;
        AgentReleaseTag = PrereleaseTagOf(info);
    }

    /// <summary>
    /// An agent version as the UI names it: in full on a preview install ("2.9.0-preview8"), where
    /// that is the build the upgrade installs, and as X.Y.Z on a stable one ("2.9.0"), where the
    /// preview it last changed in is not a release the reader would recognise.
    /// </summary>
    public static string AgentVersionForDisplay(string version) => AgentVersionForDisplay(version, AgentReleaseTag != null);

    internal static string AgentVersionForDisplay(string version, bool previewInstall) =>
        (previewInstall
            ? NetworkOptimizer.Core.Helpers.VersionUtilities.TrimLeadingV(NetworkOptimizer.Core.Helpers.VersionUtilities.StripBuildMetadata(version))
            : NetworkOptimizer.Core.Helpers.VersionUtilities.CoreVersion(version)) ?? version;

    /// <summary>
    /// "v" + the version when it is exactly a prerelease tag ("2.9.1-preview1", build metadata
    /// allowed); null for a stable version, a 0.0.0 source build, or a build commits past a tag,
    /// whose prerelease carries a dotted height ("2.9.1-preview1.3") and has no release of its own.
    /// </summary>
    internal static string? PrereleaseTagOf(string? informational)
    {
        var match = Regex.Match(informational ?? "", @"^(\d+\.\d+\.\d+-[0-9A-Za-z]+)(\+.*)?$");
        return match.Success && !match.Groups[1].Value.StartsWith("0.0.0") ? "v" + match.Groups[1].Value : null;
    }
}
