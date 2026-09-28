namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Builds the on-gateway agent install/upgrade one-liners. The single source for both the
/// command text the UI displays and the command "Run It for Me" executes over SSH, so the
/// two can never drift.
/// </summary>
public static class GatewayAgentCommands
{
    /// <summary>Shown in place of the server URL when REVERSE_PROXIED_HOST_NAME is not set.</summary>
    public const string PlaceholderServerUrl = "https://your-network-optimizer";

    private const string ScriptUrl =
        "https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-agent-gateway.sh";

    /// <summary>The --server value: the configured agent-facing URL, or the placeholder.</summary>
    public static string ServerValue(string? serverUrl) =>
        string.IsNullOrWhiteSpace(serverUrl) ? PlaceholderServerUrl : serverUrl.TrimEnd('/');

    /// <summary>
    /// First-time install one-liner. Monitoring-only gateway installer: no --lan-speed-test (the
    /// router must not host a speed-test server) and no sudo (UniFi gateways SSH in as root).
    /// </summary>
    public static string Install(string? serverUrl, string token) =>
        Install(serverUrl, token, AppVersionInfo.AgentReleaseTag);

    internal static string Install(string? serverUrl, string token, string? releaseTag) =>
        $"curl -fsSL {ScriptUrl} | bash -s -- \\\n  --server \"{ServerValue(serverUrl)}\" \\\n  --token \"{token}\""
        + (releaseTag == null ? "" : $" \\\n  --release \"{releaseTag}\"");

    /// <summary>Upgrade one-liner: same script, no token - an enrolled agent.json is kept.</summary>
    public static string Upgrade(string? serverUrl) => Upgrade(serverUrl, AppVersionInfo.AgentReleaseTag);

    internal static string Upgrade(string? serverUrl, string? releaseTag) =>
        $"curl -fsSL {ScriptUrl} | bash -s -- --server \"{ServerValue(serverUrl)}\"" + ReleaseArgument(releaseTag);

    /// <summary>
    /// Upgrade for a Docker agent. agent:latest only moves on stable releases, so a preview server
    /// points the image at its own release and a stable one points it back at latest: a box pinned
    /// during a preview would otherwise pull that preview forever. Only the image line changes, so
    /// the user's own edits to the compose file survive.
    /// </summary>
    public static string DockerUpgrade(string? releaseTag)
    {
        var tag = releaseTag == null ? "latest" : releaseTag.TrimStart('v');
        return "cd /opt/network-optimizer-agent"
            + $" && sed -i 's#\\(image: ghcr.io/ozark-connect/agent\\):.*#\\1:{tag}#' docker-compose.yml"
            + " && docker compose pull && docker compose up -d";
    }

    /// <summary>
    /// " --release \"TAG\"" when the server is a prerelease (see <see cref="AppVersionInfo.AgentReleaseTag"/>),
    /// otherwise empty, so a stable server's commands are unchanged.
    /// </summary>
    public static string ReleaseArgument(string? releaseTag) =>
        releaseTag == null ? "" : $" --release \"{releaseTag}\"";
}
