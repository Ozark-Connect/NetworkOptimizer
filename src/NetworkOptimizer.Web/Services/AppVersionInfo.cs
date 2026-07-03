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
