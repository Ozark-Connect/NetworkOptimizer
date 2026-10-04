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

    /// <summary>File-name prefix every AP Agent build shares; the suffix is the Go architecture.</summary>
    public const string BinaryPrefix = "apagent-linux-";

    /// <summary>
    /// Shell glob for every build. Never write the prefix literally in a command that also runs
    /// pgrep/pkill with <see cref="ProcessPattern"/>: the command's own shell would match it.
    /// </summary>
    public const string BinaryGlob = "apagent-linu[x]-*";

    /// <summary>Architecture-gating wrapper, so a wrong-arch AP says why instead of "Exec format error".</summary>
    public const string RemoteWrapperPath = RemoteDir + "/apagent.sh";

    /// <summary>Signing token file, mode 0600. Used by the non-procd start path, which has no env to set.</summary>
    public const string RemoteTokenPath = RemoteDir + "/token";

    public const string RemoteLogPath = RemoteDir + "/apagent.log";

    /// <summary>procd service definition. /etc is tmpfs on an AP, so this is as ephemeral as the binary.</summary>
    public const string RemoteInitScriptPath = "/etc/init.d/netopt-apagent";

    /// <summary>
    /// procd's shell include. Not enough on its own: legacy UniFi firmware (UAP-AC) ships it without
    /// the ubus "service" object rc.common needs, so the status probe checks both.
    /// </summary>
    public const string ProcdIncludePath = "/lib/functions/procd.sh";

    /// <summary>
    /// Where dropbear's sftp-server binary lives. It is a separate optional binary that only some
    /// firmware ships, and dropbear serves the SFTP subsystem by exec'ing it - so its presence is
    /// the same fact the SFTP transfer depends on.
    /// </summary>
    public const string SftpServerPath = "/usr/lib/sftp-server";

    /// <summary>Alternate sftp-server location on firmware that puts it under libexec.</summary>
    public const string SftpServerAltPath = "/usr/libexec/sftp-server";

    /// <summary>Where scp lives. It comes free with dropbear as a multi-call symlink.</summary>
    public const string ScpPath = "/usr/sbin/scp";

    /// <summary>Alternate scp location.</summary>
    public const string ScpAltPath = "/usr/bin/scp";

    /// <summary>Listener port. Must match <c>defaultPort</c> in src/apagent/config.go.</summary>
    public const int AgentPort = 8899;

    /// <summary>
    /// The build for an AP, by the names src/apagent/apagent.sh expects, or null when there is none.
    /// U7-class APs are armv7l; aarch64 is UniFi OS hardware with Wi-Fi (UDR7, UX7,
    /// UCG-Industrial). aarch64_be has no build: Go's arm64 is little-endian only.
    /// </summary>
    /// <param name="machine"><c>uname -m</c>.</param>
    /// <param name="byteOrder">"little" or "big", read from the ELF header on MIPS. The kernel
    /// reports "mips" for both byte orders (a little-endian U6-Lite says "mips").</param>
    public static string? BinaryNameFor(string? machine, string? byteOrder)
        => machine?.Trim().ToLowerInvariant() switch
        {
            "armv6l" or "armv7l" or "armv8l" => BinaryPrefix + "arm",
            "aarch64" or "arm64" => BinaryPrefix + "arm64",
            "mips" or "mips32" or "mipsel" or "mips32el" => byteOrder switch
            {
                "little" => BinaryPrefix + "mipsle",
                "big" => BinaryPrefix + "mips",
                _ => null,
            },
            _ => null,
        };

    /// <summary>Where a named build lives on the AP, beside the wrapper.</summary>
    public static string RemoteBinaryPath(string binaryName) => $"{RemoteDir}/{binaryName}";

    /// <summary>
    /// Pattern that matches the running agent and nothing else. Two traps make the obvious forms
    /// wrong: a plain -f pattern also matches the shell of the SSH command that carries it, so
    /// pkill kills its own session and pgrep reports a running agent when none exists; and -x
    /// cannot be used because Linux truncates comm to 15 characters, which clips the binary name.
    /// The bracketed first character matches the process while the literal text here does not
    /// match itself.
    /// </summary>
    public const string ProcessPattern = "[a]pagent-linux-";
}
