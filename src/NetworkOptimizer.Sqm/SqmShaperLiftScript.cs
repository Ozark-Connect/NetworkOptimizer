using System.Text;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Wraps a gateway command so it runs with the WAN's shaper lifted to a probe rate, then puts the
/// rates back. One script, one SSH session: the restore runs from an EXIT trap so a killed or
/// failed test cannot leave the link unshaped. Used for congestion profile learning, which needs
/// to measure the line rather than whatever rate Adaptive SQM is holding at that hour.
/// </summary>
public static class SqmShaperLiftScript
{
    /// <summary>
    /// Builds the wrapper script. Only the root rates that exist are touched: no IFB means Smart
    /// Queues is off and the download side is left alone; no egress HTB likewise.
    /// </summary>
    /// <param name="testCommand">The command whose stdout is the script's output.</param>
    /// <param name="interfaceName">Data-path WAN interface (validated by the caller).</param>
    /// <param name="downloadProbeMbps">Rate to hold on the IFB while the command runs.</param>
    /// <param name="uploadProbeMbps">Rate to hold on the egress HTB while the command runs.</param>
    /// <param name="rateProportionalDownloadBurst">The WAN's configured download burst mode, so the restore matches the deploy.</param>
    public static string Wrap(string testCommand, string interfaceName, int downloadProbeMbps, int uploadProbeMbps, bool rateProportionalDownloadBurst)
    {
        var validation = InputSanitizer.ValidateInterface(interfaceName);
        if (!validation.isValid)
            throw new ArgumentException(validation.error, nameof(interfaceName));
        if (downloadProbeMbps <= 0) throw new ArgumentOutOfRangeException(nameof(downloadProbeMbps));
        if (uploadProbeMbps <= 0) throw new ArgumentOutOfRangeException(nameof(uploadProbeMbps));

        var sb = new StringBuilder();
        sb.Append("#!/bin/bash\n");
        sb.Append("# Runs a WAN measurement with Adaptive SQM lifted out of the way, then restores it.\n");
        sb.Append($"INTERFACE=\"{interfaceName}\"\n");
        sb.Append($"IFB_DEVICE=\"ifb{interfaceName}\"\n");
        sb.Append($"DOWN_PROBE=\"{downloadProbeMbps}\"\n");
        sb.Append($"UP_PROBE=\"{uploadProbeMbps}\"\n");
        sb.Append($"BURST_MODE={(rateProportionalDownloadBurst ? "1" : "0")}\n");
        sb.Append('\n');
        sb.Append(ScriptGenerator.TcFunctionsText.Replace("\r\n", "\n"));
        sb.Append('\n');
        sb.Append(@"
# Current root rate of an HTB in whole Mbps, empty when the device has no HTB root class
read_root_rate_mbps() {
    local device=$1
    local rate_str=$(tc class show dev $device 2>/dev/null | grep ""class htb 1:1 root"" | grep -o ""rate [0-9]*[a-zA-Z]*"" | awk '{print $2}')
    [ -z ""$rate_str"" ] && return
    case ""$rate_str"" in
        *Gbit)  echo $(( $(echo ""$rate_str"" | sed 's/Gbit//') * 1000 )) ;;
        *Mbit)  echo ""$rate_str"" | sed 's/Mbit//' ;;
        *Kbit)  echo $(( ($(echo ""$rate_str"" | sed 's/Kbit//') + 500) / 1000 )) ;;
        *bit)   echo $(( ($(echo ""$rate_str"" | sed 's/bit//') + 500000) / 1000000 )) ;;
    esac
}

saved_down=""""
saved_up=""""
if ip link show ""$IFB_DEVICE"" >/dev/null 2>&1; then
    saved_down=$(read_root_rate_mbps ""$IFB_DEVICE"")
fi
saved_up=$(read_root_rate_mbps ""$INTERFACE"")

restore_rates() {
    if [ -n ""$saved_down"" ] && [ ""$saved_down"" -gt 0 ] 2>/dev/null; then
        update_all_tc_classes ""$IFB_DEVICE"" ""$saved_down"" ""$BURST_MODE"" >/dev/null 2>&1
    fi
    if [ -n ""$saved_up"" ] && [ ""$saved_up"" -gt 0 ] 2>/dev/null; then
        update_all_tc_classes ""$INTERFACE"" ""$saved_up"" >/dev/null 2>&1
    fi
}
trap restore_rates EXIT

if [ -n ""$saved_down"" ] && [ ""$saved_down"" -gt 0 ] 2>/dev/null; then
    update_all_tc_classes ""$IFB_DEVICE"" ""$DOWN_PROBE"" ""$BURST_MODE"" >/dev/null 2>&1
fi
if [ -n ""$saved_up"" ] && [ ""$saved_up"" -gt 0 ] 2>/dev/null; then
    update_all_tc_classes ""$INTERFACE"" ""$UP_PROBE"" >/dev/null 2>&1
fi

".Replace("\r\n", "\n"));
        sb.Append(testCommand);
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// A one-line shell command that carries the wrapper to the gateway and runs it, returning the
    /// wrapped command's exit code. The temp file name is per-interface so two WANs never collide.
    /// The script's stderr is dropped right here: the SSH runner merges stderr into the output it
    /// returns, and the binary's progress lines would land in what the caller parses as JSON.
    /// </summary>
    public static string ToRemoteCommand(string script, string interfaceName)
    {
        var validation = InputSanitizer.ValidateInterface(interfaceName);
        if (!validation.isValid)
            throw new ArgumentException(validation.error, nameof(interfaceName));
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n")));
        var path = $"/tmp/netopt-sqm-probe-{interfaceName}.sh";
        return $"echo '{b64}' | base64 -d > {path} && bash {path} 2>/dev/null; rc=$?; rm -f {path}; exit $rc";
    }
}
