namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Where the AP Agent lives on an access point, and what the server calls it.
///
/// Everything here is in tmpfs by design. The config partition behind /etc/persistent is 1 MB, so a
/// Go binary cannot live there, and controller provisioning wipes crontab, so there is no durable
/// auto-run hook either. The server pushes the agent on every boot and the AP keeps zero footprint.
/// </summary>
public static class ApAgentPaths
{
    /// <summary>tmpfs install directory. Must match <c>defaultInstallDir</c> in src/apagent/config.go.</summary>
    public const string RemoteDir = "/tmp/netopt-apagent";

    /// <summary>The armv7 binary as the wrapper expects to find it (src/apagent/apagent.sh).</summary>
    public const string RemoteBinaryPath = RemoteDir + "/apagent-linux-arm";

    /// <summary>Architecture-gating wrapper, so a wrong-arch AP says why instead of "Exec format error".</summary>
    public const string RemoteWrapperPath = RemoteDir + "/apagent.sh";

    /// <summary>Bearer token file, mode 0600. Used by the non-procd start path, which has no env to set.</summary>
    public const string RemoteTokenPath = RemoteDir + "/token";

    public const string RemoteLogPath = RemoteDir + "/apagent.log";

    /// <summary>procd service definition. /etc is tmpfs on an AP, so this is as ephemeral as the binary.</summary>
    public const string RemoteInitScriptPath = "/etc/init.d/netopt-apagent";

    /// <summary>Presence of this file is how the server knows procd is available to supervise with.</summary>
    public const string ProcdIncludePath = "/lib/functions/procd.sh";

    /// <summary>Listener port. Must match <c>defaultPort</c> in src/apagent/config.go.</summary>
    public const int AgentPort = 8899;

    /// <summary>Name of the binary staged in the server's own tools directory.</summary>
    public const string LocalBinaryName = "apagent-linux-arm";
}
