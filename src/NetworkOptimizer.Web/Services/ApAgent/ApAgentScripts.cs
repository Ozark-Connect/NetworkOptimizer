using System.Text;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// The shell the server runs on an access point: the one-round-trip status probe and its parser,
/// the procd service definition, and the start, stop, and removal commands.
///
/// Kept apart from the service so the exact text is testable without an AP.
/// </summary>
public static class ApAgentScripts
{
    /// <summary>
    /// Machine architectures with an AP Agent build. Every measured U7-class AP is armv7l; the
    /// Makefile deliberately builds no arm64 target, so aarch64 hardware is unsupported rather than
    /// broken, and says so.
    /// </summary>
    private static readonly HashSet<string> SupportedMachines = new(StringComparer.OrdinalIgnoreCase)
    {
        "armv6l", "armv7l", "armv8l",
    };

    /// <summary>Whether an AP Agent build exists for a machine architecture.</summary>
    public static bool SupportsArchitecture(string? machine)
        => !string.IsNullOrWhiteSpace(machine) && SupportedMachines.Contains(machine.Trim());

    /// <summary>Why an architecture is unsupported, in words an operator can act on.</summary>
    public static string UnsupportedReason(string? machine)
        => string.IsNullOrWhiteSpace(machine)
            ? "Could not read this access point's architecture over SSH."
            : $"This access point reports {machine.Trim()}. The AP Agent is built for 32-bit ARM (armv7l) only today.";

    /// <summary>
    /// Everything the server needs about an AP in one command. An AP is a slow SSH target and each
    /// session costs a full handshake, so the fields are gathered together rather than one at a time.
    /// </summary>
    public static string StatusProbeCommand()
    {
        return
            "echo '---ARCH---'; uname -m 2>/dev/null; " +
            "echo '---MODEL---'; sed -n 's/^board\\.name=//p' /etc/board.info 2>/dev/null | head -1; " +
            "echo '---FIRMWARE---'; head -1 /usr/lib/version 2>/dev/null; " +
            $"echo '---PROCD---'; test -f {ApAgentPaths.ProcdIncludePath} && echo present || echo absent; " +
            $"echo '---BINARY---'; test -x {ApAgentPaths.RemoteBinaryPath} && echo exists || echo missing; " +
            $"echo '---WRAPPER---'; test -x {ApAgentPaths.RemoteWrapperPath} && echo exists || echo missing; " +
            $"echo '---PROCESS---'; pgrep -f {ApAgentPaths.RemoteBinaryPath} > /dev/null 2>&1 && echo running || echo stopped; " +
            $"echo '---VERSION---'; {ApAgentPaths.RemoteWrapperPath} -version 2>/dev/null; " +
            $"echo '---BINARY_VERSION---'; {ApAgentPaths.RemoteWrapperPath} -binary-version 2>/dev/null; " +
            $"echo '---MD5---'; md5sum {ApAgentPaths.RemoteBinaryPath} 2>/dev/null | cut -d' ' -f1";
    }

    /// <summary>Reads the status probe's delimited output.</summary>
    /// <param name="output">Raw command output.</param>
    /// <param name="success">Whether the SSH command itself succeeded.</param>
    public static ApAgentSshStatus ParseStatus(string output, bool success)
    {
        var status = new ApAgentSshStatus { Reachable = success };
        if (!success)
        {
            status.Error = string.IsNullOrWhiteSpace(output) ? "SSH command failed" : output.Trim();
            return status;
        }

        var sections = ParseDelimitedOutput(output);

        status.Machine = Section(sections, "ARCH");
        status.Model = Section(sections, "MODEL");
        status.Firmware = Section(sections, "FIRMWARE");
        status.SupportedArchitecture = SupportsArchitecture(status.Machine);
        status.ProcdAvailable = Section(sections, "PROCD") == "present";
        status.BinaryDeployed = Section(sections, "BINARY") == "exists";
        status.WrapperDeployed = Section(sections, "WRAPPER") == "exists";
        status.IsRunning = Section(sections, "PROCESS") == "running";
        status.Version = Section(sections, "VERSION");
        status.BinaryMd5 = Section(sections, "MD5");

        if (int.TryParse(Section(sections, "BINARY_VERSION"), out var binaryVersion))
            status.DeployedBinaryVersion = binaryVersion;

        return status;
    }

    /// <summary>
    /// The procd service definition. The token goes in the service environment, never on the
    /// command line, where ps would show it to every user on the AP. /etc is tmpfs here, so this
    /// file is exactly as ephemeral as the binary it starts.
    /// </summary>
    public static string InitScript(string token)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh /etc/rc.common\n");
        sb.Append("# Network Optimizer AP Agent. Ephemeral by design: the server rewrites this on every boot.\n");
        sb.Append("USE_PROCD=1\n");
        sb.Append("START=95\n");
        sb.Append("STOP=10\n");
        sb.Append("\n");
        sb.Append("start_service() {\n");
        sb.Append("    procd_open_instance\n");
        sb.Append($"    procd_set_param command {ApAgentPaths.RemoteWrapperPath}\n");
        sb.Append($"    procd_set_param env APAGENT_TOKEN={ShellQuote(token)}\n");
        sb.Append("    procd_set_param respawn 3600 5 0\n");
        sb.Append("    procd_set_param stdout 1\n");
        sb.Append("    procd_set_param stderr 1\n");
        sb.Append("    procd_close_instance\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Starts the agent. procd supervises it where procd is available; otherwise the agent is
    /// backgrounded and reads its token from a 0600 file, which keeps it off the command line the
    /// same way the service environment does.
    /// </summary>
    public static string StartCommand(bool procdAvailable)
        => procdAvailable
            ? $"chmod +x {ApAgentPaths.RemoteInitScriptPath} && {ApAgentPaths.RemoteInitScriptPath} start >/dev/null 2>&1; " + VerifyRunningCommand()
            : $"nohup {ApAgentPaths.RemoteWrapperPath} -token-file {ApAgentPaths.RemoteTokenPath} >> {ApAgentPaths.RemoteLogPath} 2>&1 & " + VerifyRunningCommand();

    /// <summary>Stops the agent, through procd where it started it.</summary>
    public static string StopCommand(bool procdAvailable)
        => (procdAvailable ? $"test -x {ApAgentPaths.RemoteInitScriptPath} && {ApAgentPaths.RemoteInitScriptPath} stop >/dev/null 2>&1; " : "")
           + $"pkill -f {ApAgentPaths.RemoteBinaryPath} 2>/dev/null; sleep 1; "
           + $"pkill -0 -f {ApAgentPaths.RemoteBinaryPath} 2>/dev/null && pkill -9 -f {ApAgentPaths.RemoteBinaryPath}; true";

    /// <summary>Stops the agent and clears everything it wrote. A reboot does the same thing.</summary>
    public static string RemoveCommand(bool procdAvailable)
        => StopCommand(procdAvailable)
           + $"; rm -rf {ApAgentPaths.RemoteDir}; rm -f {ApAgentPaths.RemoteInitScriptPath}; true";

    /// <summary>Reports whether the agent came up, with the tail of its log when it did not.</summary>
    public static string VerifyRunningCommand()
        => $"sleep 2; if pgrep -f {ApAgentPaths.RemoteBinaryPath} > /dev/null 2>&1; then echo started; "
           + $"else echo failed; tail -5 {ApAgentPaths.RemoteLogPath} 2>/dev/null; fi";

    /// <summary>Writes a text file on the AP by piping base64 through the shell.</summary>
    /// <param name="content">File content.</param>
    /// <param name="remotePath">Destination path.</param>
    /// <param name="mode">chmod mode to apply afterwards.</param>
    public static string WriteFileCommand(string content, string remotePath, string mode)
    {
        var base64 = GatewayFile.ToBase64(content);
        return $"echo {base64} | base64 -d > {remotePath} && chmod {mode} {remotePath}";
    }

    /// <summary>Splits the status probe's delimited output into its sections.</summary>
    internal static Dictionary<string, string> ParseDelimitedOutput(string output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentKey = null;
        var currentValue = new List<string>();

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 6 && trimmed.StartsWith("---", StringComparison.Ordinal) && trimmed.EndsWith("---", StringComparison.Ordinal))
            {
                if (currentKey != null)
                    sections[currentKey] = string.Join("\n", currentValue);

                currentKey = trimmed.Trim('-');
                currentValue.Clear();
            }
            else if (currentKey != null)
            {
                currentValue.Add(line);
            }
        }

        if (currentKey != null)
            sections[currentKey] = string.Join("\n", currentValue);

        return sections;
    }

    private static string? Section(Dictionary<string, string> sections, string key)
    {
        if (!sections.TryGetValue(key, out var value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Single-quotes a value for the shell, so a token can hold anything the RNG produced.</summary>
    internal static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''") + "'";
}
