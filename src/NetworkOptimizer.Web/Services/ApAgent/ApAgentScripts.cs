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
    /// Whether an AP Agent build exists for a machine architecture, in either byte order. The
    /// Makefile deliberately builds no arm64 target, so aarch64 hardware is unsupported rather than
    /// broken, and says so.
    /// </summary>
    public static bool SupportsArchitecture(string? machine)
        => ApAgentPaths.BinaryNameFor(machine, "little") != null || ApAgentPaths.BinaryNameFor(machine, "big") != null;

    /// <summary>Why an architecture is unsupported, in words an operator can act on.</summary>
    /// <param name="machine"><c>uname -m</c>.</param>
    /// <param name="byteOrder">The probe's byte order reading, when the machine is supported but it was unreadable.</param>
    public static string UnsupportedReason(string? machine, string? byteOrder = null)
    {
        if (string.IsNullOrWhiteSpace(machine))
            return "Could not read this access point's architecture over SSH.";
        if (SupportsArchitecture(machine) && byteOrder == null)
            return $"This access point reports {machine.Trim()}, but its byte order could not be read over SSH.";
        return $"This access point reports {machine.Trim()}. The AP Agent is built for 32-bit ARM (armv7l) and 32-bit MIPS.";
    }

    /// <summary>
    /// Everything the server needs about an AP in one command. An AP is a slow SSH target and each
    /// session costs a full handshake, so the fields are gathered together rather than one at a time.
    /// </summary>
    public static string StatusProbeCommand()
    {
        var builds = $"{ApAgentPaths.RemoteDir}/{ApAgentPaths.BinaryGlob}";
        return
            "AP_M=$(uname -m 2>/dev/null); " +
            "echo '---ARCH---'; echo \"$AP_M\"; " +
            // uname says "mips" for both byte orders; busybox's ELF EI_DATA byte says which. dd and
            // printf because od is absent on U7. Runs on MIPS only.
            "echo '---BYTE_ORDER---'; case \"$AP_M\" in mips*) b=$(dd if=/bin/busybox bs=1 skip=5 count=1 2>/dev/null); " +
            "test \"$b\" = \"$(printf '\\001')\" && echo little; test \"$b\" = \"$(printf '\\002')\" && echo big;; esac; " +
            "echo '---MODEL---'; sed -n 's/^board\\.name=//p' /etc/board.info 2>/dev/null | head -1; " +
            "echo '---FIRMWARE---'; head -1 /usr/lib/version 2>/dev/null; " +
            $"echo '---PROCD---'; test -f {ApAgentPaths.ProcdIncludePath} && ubus -t 2 list service >/dev/null 2>&1 && echo present || echo absent; " +
            // Every build present, by path; the parser picks this AP's by name. The glob keeps the
            // literal binary name out of this command line, where pgrep below would match it.
            $"echo '---BINARY---'; for f in {builds}; do test -x \"$f\" && echo \"$f\"; done; " +
            $"echo '---WRAPPER---'; test -x {ApAgentPaths.RemoteWrapperPath} && echo exists || echo missing; " +
            $"echo '---PROCESS---'; pgrep -f {ApAgentPaths.ProcessPattern} > /dev/null 2>&1 && echo running || echo stopped; " +
            $"echo '---VERSION---'; {ApAgentPaths.RemoteWrapperPath} -version 2>/dev/null; " +
            $"echo '---BINARY_VERSION---'; {ApAgentPaths.RemoteWrapperPath} -binary-version 2>/dev/null; " +
            $"echo '---MD5---'; md5sum {builds} 2>/dev/null; " +
            // Braceless on purpose: at equal precedence "a || b && c || d" parses as ((a || b) && c) || d
            // on busybox ash, which is what we want. POSIX marks -o obsolescent, and braces would have
            // to be written {{ }} inside this interpolated string.
            $"echo '---SFTP---'; test -f {ApAgentPaths.SftpServerPath} || test -f {ApAgentPaths.SftpServerAltPath} && echo present || echo absent; " +
            $"echo '---SCP---'; test -x {ApAgentPaths.ScpPath} || test -x {ApAgentPaths.ScpAltPath} && echo present || echo absent";
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
        status.ByteOrder = Section(sections, "BYTE_ORDER");
        status.BinaryName = ApAgentPaths.BinaryNameFor(status.Machine, status.ByteOrder);
        status.Model = Section(sections, "MODEL");
        status.Firmware = Section(sections, "FIRMWARE");
        status.SupportedArchitecture = status.BinaryName != null;
        status.ProcdAvailable = Section(sections, "PROCD") == "present";
        status.WrapperDeployed = Section(sections, "WRAPPER") == "exists";
        status.IsRunning = Section(sections, "PROCESS") == "running";
        status.Version = Section(sections, "VERSION");

        if (status.BinaryName != null)
        {
            status.BinaryDeployed = Lines(sections, "BINARY").Any(l => FileName(l) == status.BinaryName);
            // md5sum prints "<hash>  <path>" per file.
            status.BinaryMd5 = Lines(sections, "MD5")
                .Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length == 2 && FileName(p[1]) == status.BinaryName)
                .Select(p => p[0])
                .FirstOrDefault();
        }
        status.SftpAvailable = Section(sections, "SFTP") == "present";
        status.ScpAvailable = Section(sections, "SCP") == "present";

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
           + $"pkill -f {ApAgentPaths.ProcessPattern} 2>/dev/null; sleep 1; "
           + $"pgrep -f {ApAgentPaths.ProcessPattern} >/dev/null 2>&1 && pkill -9 -f {ApAgentPaths.ProcessPattern}; true";

    /// <summary>Stops the agent and clears everything it wrote. A reboot does the same thing.</summary>
    public static string RemoveCommand(bool procdAvailable)
        => StopCommand(procdAvailable)
           + $"; rm -rf {ApAgentPaths.RemoteDir}; rm -f {ApAgentPaths.RemoteInitScriptPath}; true";

    /// <summary>
    /// Reports whether the agent came up, with the tail of its log when it did not. The first check
    /// stays at 2 s so an agent that exits at once is still caught; a slow MIPS AP gets up to 8 s more.
    /// </summary>
    public static string VerifyRunningCommand()
        => $"sleep 2; for i in 1 2 3 4 5 6 7 8 9; do pgrep -f {ApAgentPaths.ProcessPattern} > /dev/null 2>&1 && break; "
           + "test $i = 9 || sleep 1; done; "
           + $"if pgrep -f {ApAgentPaths.ProcessPattern} > /dev/null 2>&1; then echo started; "
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

    private static IEnumerable<string> Lines(Dictionary<string, string> sections, string key)
        => (Section(sections, key) ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string FileName(string path) => path.Trim()[(path.Trim().LastIndexOf('/') + 1)..];

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
