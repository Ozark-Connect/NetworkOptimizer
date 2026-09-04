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
        $"curl -fsSL {ScriptUrl} | bash -s -- \\\n  --server \"{ServerValue(serverUrl)}\" \\\n  --token \"{token}\"";

    /// <summary>Upgrade one-liner: same script, no token - an enrolled agent.json is kept.</summary>
    public static string Upgrade(string? serverUrl) =>
        $"curl -fsSL {ScriptUrl} | bash -s -- --server \"{ServerValue(serverUrl)}\"";
}
