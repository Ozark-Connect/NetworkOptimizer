using System.Text;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Shell builders for sending AT commands straight to a modem's device node over SSH, shared by
/// the GL.iNet transport (<see cref="GlModemTransport"/>) and the generic Quectel transport.
/// Every string built here runs on the router, so device paths are validated and AT commands
/// may not carry a single quote or a line break.
/// </summary>
public static class AtDeviceShell
{
    /// <summary>Seconds a read waits for the modem's final result code.</summary>
    public const int ReadSeconds = 3;

    /// <summary>
    /// The loop that copies the modem's answer to stdout and stops at its final result code.
    /// Runs inside a single-quoted <c>sh -c</c>, so it holds no single quote.
    /// </summary>
    private static string ReadUntilFinalResult(string readRedirect) =>
        $"while IFS= read -r l{readRedirect}; do echo \"$l\"; " +
        "case \"$l\" in OK*|ERROR*|+CME\\ ERROR*|+CMS\\ ERROR*) break;; esac; done";

    private static readonly Regex DevicePattern = new(@"^/dev/[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>Whether a path is safe to interpolate into a shell command as a device node.</summary>
    public static bool IsValidDevice(string? device) =>
        !string.IsNullOrEmpty(device) && DevicePattern.IsMatch(device);

    /// <summary>
    /// Write the command, then read the answer on a second open of the device. This is the form
    /// GL.iNet's MHI routers run, and <paramref name="timeoutCommand"/> wraps only the read.
    /// </summary>
    /// <param name="device">A validated device node, e.g. <c>/dev/mhi_DUN</c>.</param>
    /// <param name="atCommand">The AT command, without the trailing carriage return.</param>
    /// <param name="timeoutCommand"><c>timeout</c>, or <c>_to</c> when <see cref="TimeoutPrelude"/> is in the script.</param>
    public static string BuildTwoOpenCommand(string device, string atCommand, string timeoutCommand) =>
        $"echo -ne '{ValidAt(atCommand)}\\r' > {ValidDevice(device)}; " +
        $"{timeoutCommand} {ReadSeconds} sh -c '{ReadUntilFinalResult("")} < {device}'";

    /// <summary>
    /// Open the device once for read and write, send the command, and read the answer on the same
    /// descriptor. A device that allows one opener at a time (WWAN AT ports) or drops what arrives
    /// between two opens (ttys) loses the answer to the two-open form. The device and command go in
    /// as positional arguments, so neither is quoted inside the script.
    /// </summary>
    public static string BuildSingleOpenCommand(string device, string atCommand) =>
        $"_to {ReadSeconds} sh -c 'exec 3<>\"$1\"; printf \"%s\\r\" \"$2\" >&3; " +
        $"{ReadUntilFinalResult(" <&3")}' sh {ValidDevice(device)} '{ValidAt(atCommand)}'";

    /// <summary>
    /// Defines <c>_to SECONDS CMD...</c>. It uses <c>timeout</c> when a working one exists and
    /// otherwise runs the command in the background with a watchdog that kills it. The probe runs
    /// <c>timeout 1 true</c> rather than testing for the name: BusyBox before 1.30 has a
    /// <c>timeout</c> that rejects positional seconds. The watchdog's output goes to /dev/null, or
    /// its orphaned <c>sleep</c> would hold the SSH session open for the full timeout.
    /// </summary>
    public const string TimeoutPrelude =
        "if timeout 1 true >/dev/null 2>&1; then _to() { timeout \"$@\"; }; " +
        "else _to() { t=$1; shift; \"$@\" & p=$!; " +
        "(sleep \"$t\"; kill $p 2>/dev/null) >/dev/null 2>&1 & w=$!; " +
        "wait $p; r=$?; kill $w 2>/dev/null; return $r; }; fi";

    /// <summary>
    /// Split marker-delimited script output into sections keyed by each marker's key. Text before
    /// the first marker is dropped; a marker that never printed leaves no entry.
    /// </summary>
    public static Dictionary<string, string> SplitSections(
        string output, IEnumerable<(string Key, string Marker)> markers)
    {
        var markerList = markers.ToList();
        var sections = new Dictionary<string, string>();
        string? current = null;
        var buffer = new StringBuilder();

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            var match = markerList.FirstOrDefault(m => trimmed == m.Marker);
            if (match.Marker != null)
            {
                if (current != null) sections[current] = buffer.ToString();
                current = match.Key;
                buffer.Clear();
                continue;
            }
            buffer.AppendLine(line);
        }

        if (current != null) sections[current] = buffer.ToString();
        return sections;
    }

    private static string ValidDevice(string device) =>
        IsValidDevice(device) ? device : throw new ArgumentException($"Invalid modem device: {device}");

    private static string ValidAt(string atCommand) =>
        atCommand.Contains('\'') || atCommand.Contains('\n') || atCommand.Contains('\r')
            ? throw new ArgumentException($"Unsupported characters in AT command: {atCommand}")
            : atCommand;
}
