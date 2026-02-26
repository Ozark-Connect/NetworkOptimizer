using System.Globalization;
using System.Text;
using NetworkOptimizer.Sqm.Models;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Generates shell scripts for SQM deployment on UniFi devices.
/// Creates self-contained boot scripts that survive firmware upgrades.
/// </summary>
public class ScriptGenerator
{
    private readonly SqmConfiguration _config;
    private readonly string _name; // Normalized name for files (e.g., "wan1", "wan2")
    private readonly int _initialDelaySeconds; // Delay before first speedtest (for staggering multiple WANs)

    public ScriptGenerator(SqmConfiguration config, int initialDelaySeconds = 60)
    {
        _config = config;
        _initialDelaySeconds = initialDelaySeconds;
        // Sanitize connection name for safe use in filenames and shell variables
        // Security: prevents command injection via filename/path manipulation
        _name = string.IsNullOrWhiteSpace(config.ConnectionName)
            ? InputSanitizer.SanitizeConnectionName(config.Interface)
            : InputSanitizer.SanitizeConnectionName(config.ConnectionName);
    }

    /// <summary>
    /// Format a double using invariant culture to ensure consistent decimal point (not comma)
    /// regardless of system locale. Critical for shell script generation.
    /// Rounds to 10 decimal places to avoid IEEE 754 artifacts like 0.30000000000000004.
    /// </summary>
    private static string Inv(double value) => Math.Round(value, 10).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Generate all scripts required for SQM deployment.
    /// Returns a single self-contained boot script that creates everything else.
    /// </summary>
    public Dictionary<string, string> GenerateAllScripts(Dictionary<string, string> baseline)
    {
        return new Dictionary<string, string>
        {
            [$"20-sqm-{_name}.sh"] = GenerateBootScript(baseline)
        };
    }

    /// <summary>
    /// Get the boot script filename for this configuration
    /// </summary>
    public string GetBootScriptName() => $"20-sqm-{_name}.sh";

    /// <summary>
    /// Generate the self-contained boot script that:
    /// 1. Installs dependencies (speedtest, bc)
    /// 2. Creates /data/sqm/ directory
    /// 3. Writes speedtest and ping scripts via heredoc
    /// 4. Sets up IFB device and TC classes
    /// 5. Configures crontab entries
    /// </summary>
    public string GenerateBootScript(Dictionary<string, string> baseline)
    {
        var sb = new StringBuilder();

        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine($"# SQM Boot Script for {_config.ConnectionName} ({_config.Interface})");
        sb.AppendLine("# This script is self-contained and will recreate all SQM components on boot.");
        sb.AppendLine("# Safe to run after firmware upgrades - udm-boot executes scripts in /data/on_boot.d/");
        sb.AppendLine();
        sb.AppendLine($"SQM_NAME=\"{_name}\"");
        sb.AppendLine($"INTERFACE=\"{_config.Interface}\"");
        sb.AppendLine($"IFB_DEVICE=\"ifb{_config.Interface}\"");
        sb.AppendLine("SQM_DIR=\"/data/sqm\"");
        sb.AppendLine("SPEEDTEST_SCRIPT=\"$SQM_DIR/${SQM_NAME}-speedtest.sh\"");
        sb.AppendLine("PING_SCRIPT=\"$SQM_DIR/${SQM_NAME}-ping.sh\"");
        sb.AppendLine("RESULT_FILE=\"$SQM_DIR/${SQM_NAME}-result.txt\"");
        sb.AppendLine("LOG_FILE=\"/var/log/sqm-${SQM_NAME}.log\"");
        sb.AppendLine();
        sb.AppendLine("echo \"[$(date)] SQM boot script starting for $SQM_NAME ($INTERFACE)...\" >> $LOG_FILE");
        sb.AppendLine();

        // Section 1: Install dependencies
        sb.AppendLine("# ============================================");
        sb.AppendLine("# Section 1: Install Dependencies");
        sb.AppendLine("# ============================================");
        sb.AppendLine();
        sb.AppendLine("# Install official Ookla speedtest if not present");
        sb.AppendLine("if ! which speedtest > /dev/null 2>&1; then");
        sb.AppendLine("    echo \"Installing Ookla speedtest...\" >> $LOG_FILE");
        sb.AppendLine("    # Remove UniFi's speedtest if present");
        sb.AppendLine("    apt-get remove -y speedtest 2>/dev/null || true");
        sb.AppendLine("    # Install official Speedtest by Ookla");
        sb.AppendLine("    curl -s https://packagecloud.io/install/repositories/ookla/speedtest-cli/script.deb.sh | bash");
        sb.AppendLine("    apt-get install -y speedtest");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Install bc if not present");
        sb.AppendLine("if ! which bc > /dev/null 2>&1; then");
        sb.AppendLine("    echo \"Installing bc...\" >> $LOG_FILE");
        sb.AppendLine("    apt-get install -y bc");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Install jq if not present");
        sb.AppendLine("if ! which jq > /dev/null 2>&1; then");
        sb.AppendLine("    echo \"Installing jq...\" >> $LOG_FILE");
        sb.AppendLine("    apt-get install -y jq");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Section 2: Create directories
        sb.AppendLine("# ============================================");
        sb.AppendLine("# Section 2: Create Directories");
        sb.AppendLine("# ============================================");
        sb.AppendLine();
        sb.AppendLine("mkdir -p $SQM_DIR");
        sb.AppendLine();

        // Section 3: Write speedtest script via heredoc
        sb.AppendLine("# ============================================");
        sb.AppendLine("# Section 3: Create Speedtest Adjustment Script");
        sb.AppendLine("# ============================================");
        sb.AppendLine();
        sb.AppendLine("cat > \"$SPEEDTEST_SCRIPT\" << 'SPEEDTEST_EOF'");
        sb.Append(GenerateSpeedtestScript(baseline));
        sb.AppendLine("SPEEDTEST_EOF");
        sb.AppendLine("chmod +x \"$SPEEDTEST_SCRIPT\"");
        sb.AppendLine();

        // Section 4: Write ping script via heredoc
        sb.AppendLine("# ============================================");
        sb.AppendLine("# Section 4: Create Ping Adjustment Script");
        sb.AppendLine("# ============================================");
        sb.AppendLine();
        sb.AppendLine("cat > \"$PING_SCRIPT\" << 'PING_EOF'");
        sb.Append(GeneratePingScript(baseline));
        sb.AppendLine("PING_EOF");
        sb.AppendLine("chmod +x \"$PING_SCRIPT\"");
        sb.AppendLine();

        // Section 5: Configure crontab
        sb.AppendLine("# ============================================");
        sb.AppendLine("# Section 5: Configure Crontab");
        sb.AppendLine("# ============================================");
        sb.AppendLine();

        // Cron environment setup (PATH for tc, HOME for speedtest)
        const string cronEnv = "export PATH=\\\"/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin\\\"; export HOME=/root;";

        // Remove existing cron entries for this WAN and add fresh ones
        // This ensures schedule changes take effect on redeploy
        sb.AppendLine("# Remove existing cron entries for this WAN (to allow schedule updates)");
        sb.AppendLine("crontab -l 2>/dev/null | grep -v \"$SPEEDTEST_SCRIPT\" | grep -v \"$PING_SCRIPT\" | crontab -");
        sb.AppendLine();

        // Build the time exclusion check for ping script
        var exclusionCheck = new StringBuilder();
        exclusionCheck.Append("if [");
        for (int i = 0; i < _config.SpeedtestSchedule.Count; i++)
        {
            var parts = _config.SpeedtestSchedule[i].Split(' ');
            if (parts.Length >= 2)
            {
                var minute = parts[0];
                var hour = parts[1];
                exclusionCheck.Append($" \\\"\\$(date +\\%H:\\%M)\\\" != \\\"{hour.PadLeft(2, '0')}:{minute.PadLeft(2, '0')}\\\"");
                if (i < _config.SpeedtestSchedule.Count - 1)
                {
                    exclusionCheck.Append(" ] && [");
                }
            }
        }
        exclusionCheck.Append(" ]; then $PING_SCRIPT >> $LOG_FILE 2>&1; fi");

        // Add speedtest and ping cron jobs
        sb.AppendLine("# Add speedtest and ping cron jobs");
        sb.Append("(crontab -l 2>/dev/null");
        foreach (var schedule in _config.SpeedtestSchedule)
        {
            sb.Append($"; echo \"{schedule} {cronEnv} $SPEEDTEST_SCRIPT >> $LOG_FILE 2>&1\"");
        }
        sb.Append($"; echo \"*/{_config.PingAdjustmentInterval} * * * * {cronEnv} {exclusionCheck}\"");
        sb.AppendLine(") | crontab -");
        sb.AppendLine("echo \"[$(date)] Cron jobs configured for $SQM_NAME\" >> $LOG_FILE");
        sb.AppendLine();

        // Section 6: Schedule initial calibration
        sb.AppendLine("# ============================================");
        sb.AppendLine("# Section 6: Schedule Initial Calibration");
        sb.AppendLine("# ============================================");
        sb.AppendLine();
        sb.AppendLine("# Cancel any previously scheduled speedtest timers for this WAN");
        sb.AppendLine("for unit in $(systemctl list-units --type=timer --state=active --no-legend | grep -E 'run-.*speedtest' | awk '{print $1}'); do");
        sb.AppendLine("    if systemctl cat \"$unit\" 2>/dev/null | grep -q \"$SPEEDTEST_SCRIPT\"; then");
        sb.AppendLine("        echo \"[$(date)] Canceling previous timer: $unit\" >> $LOG_FILE");
        sb.AppendLine("        systemctl stop \"$unit\" 2>/dev/null || true");
        sb.AppendLine("    fi");
        sb.AppendLine("done");
        sb.AppendLine();
        // !!! TODO: UNCOMMENT BEFORE MERGING TO MAIN !!!
        // Auto speedtest suppressed for testing on metered WAN connections
        // sb.AppendLine($"# Schedule speedtest calibration {_initialDelaySeconds} seconds after boot");
        // sb.AppendLine($"echo \"[$(date)] Scheduling initial SQM calibration in {_initialDelaySeconds} seconds...\" >> $LOG_FILE");
        // sb.AppendLine($"systemd-run --on-active={_initialDelaySeconds}sec --timer-property=AccuracySec=1s \\");
        // sb.AppendLine("  --setenv=PATH=\"$PATH\" \\");
        // sb.AppendLine("  --setenv=HOME=/root \\");
        // sb.AppendLine("  \"$SPEEDTEST_SCRIPT\"");
        sb.AppendLine();
        sb.AppendLine("echo \"[$(date)] SQM boot script completed for $SQM_NAME\" >> $LOG_FILE");

        return sb.ToString();
    }

    /// <summary>
    /// Generate the speedtest adjustment script content (embedded in boot script)
    /// </summary>
    private string GenerateSpeedtestScript(Dictionary<string, string> baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine("# SQM Speedtest Adjustment Script");
        sb.AppendLine($"# Connection: {_config.ConnectionName} ({_config.Interface})");
        sb.AppendLine();

        // Variables
        sb.AppendLine("# Configuration");
        sb.AppendLine($"INTERFACE=\"{_config.Interface}\"");
        sb.AppendLine($"IFB_DEVICE=\"ifb{_config.Interface}\"");
        sb.AppendLine($"MAX_DOWNLOAD_SPEED=\"{_config.MaxDownloadSpeed}\"");
        sb.AppendLine($"ABSOLUTE_MAX_DOWNLOAD_SPEED=\"{_config.AbsoluteMaxDownloadSpeed}\"");
        sb.AppendLine($"MIN_DOWNLOAD_SPEED=\"{_config.MinDownloadSpeed}\"");
        sb.AppendLine($"DOWNLOAD_SPEED_MULTIPLIER=\"{Inv(_config.OverheadMultiplier)}\"");
        sb.AppendLine($"RESULT_FILE=\"/data/sqm/{_name}-result.txt\"");
        sb.AppendLine($"LOG_FILE=\"/var/log/sqm-{_name}.log\"");
        sb.AppendLine();

        // Baseline data
        sb.AppendLine("# Baseline speeds by day of week (0=Mon, 6=Sun) and hour");
        sb.AppendLine("declare -A BASELINE");
        foreach (var (key, value) in baseline.OrderBy(b => b.Key))
        {
            sb.AppendLine($"BASELINE[{key}]=\"{value}\"");
        }
        sb.AppendLine();

        // Check for speedtest
        sb.AppendLine("# Check if speedtest is installed");
        sb.AppendLine("if ! which speedtest > /dev/null 2>&1; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: speedtest not found\" >> $LOG_FILE");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();

        sb.AppendLine("echo \"[$(date)] Starting speedtest adjustment on $INTERFACE...\" >> $LOG_FILE");
        sb.AppendLine();

        // Verify IFB device exists (created by UniFi Smart Queues)
        sb.AppendLine("# Verify IFB device exists (created by UniFi Smart Queues)");
        sb.AppendLine("if ! ip link show \"$IFB_DEVICE\" &>/dev/null; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: IFB device $IFB_DEVICE does not exist. Smart Queues may not be enabled in UniFi Network settings.\" >> $LOG_FILE");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();

        // TC update function
        sb.AppendLine(GetTcUpdateFunction());
        sb.AppendLine();

        // Set absolute max before speedtest for clean, unlimited test
        sb.AppendLine("# Set SQM to absolute max before speedtest for accurate measurement");
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $ABSOLUTE_MAX_DOWNLOAD_SPEED");
        sb.AppendLine();

        // Run speedtest
        var serverIdArg = string.IsNullOrEmpty(_config.PreferredSpeedtestServerId)
            ? ""
            : $" --server-id={_config.PreferredSpeedtestServerId}";
        sb.AppendLine("# Run speedtest");
        sb.AppendLine($"speedtest_output=$(speedtest --accept-license --accept-gdpr --format=json --interface=$INTERFACE{serverIdArg})");
        sb.AppendLine();
        sb.AppendLine("# Parse download speed (bytes/sec to Mbps)");
        sb.AppendLine("download_speed_bytes=$(echo \"$speedtest_output\" | jq .download.bandwidth)");
        sb.AppendLine("download_speed_mbps=$(echo \"scale=0; $download_speed_bytes * 8 / 1000000\" | bc)");
        sb.AppendLine();
        sb.AppendLine("echo \"[$(date)] Measured: $download_speed_mbps Mbps\" >> $LOG_FILE");
        sb.AppendLine();

        // Apply floor
        sb.AppendLine("# Apply minimum floor");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps < MIN_DOWNLOAD_SPEED ? MIN_DOWNLOAD_SPEED : download_speed_mbps))");
        sb.AppendLine();

        // Baseline blending
        sb.AppendLine(GetBaselineBlendingLogic());
        sb.AppendLine();

        // Apply ceiling
        sb.AppendLine("# Apply ceiling");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps > MAX_DOWNLOAD_SPEED ? MAX_DOWNLOAD_SPEED : download_speed_mbps))");
        sb.AppendLine();

        // Apply 95% cap
        sb.AppendLine("# Apply 95% cap");
        sb.AppendLine("max_adjusted_rate=$(echo \"$MAX_DOWNLOAD_SPEED * 0.95 / 1\" | bc)");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps > max_adjusted_rate ? max_adjusted_rate : download_speed_mbps))");
        sb.AppendLine();

        // Save result and apply
        sb.AppendLine("# Save result for ping script");
        sb.AppendLine("echo \"Measured download speed: $download_speed_mbps Mbps\" > \"$RESULT_FILE\"");
        sb.AppendLine();
        sb.AppendLine("# Apply TC classes");
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $download_speed_mbps");
        sb.AppendLine();
        sb.AppendLine("echo \"[$(date)] Adjusted to $download_speed_mbps Mbps\" >> $LOG_FILE");

        return sb.ToString();
    }

    /// <summary>
    /// Generate the ping adjustment script content (embedded in boot script)
    /// </summary>
    private string GeneratePingScript(Dictionary<string, string> baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine("# SQM Ping Adjustment Script");
        sb.AppendLine($"# Connection: {_config.ConnectionName} ({_config.Interface})");
        sb.AppendLine();

        // Variables
        sb.AppendLine("# Configuration");
        sb.AppendLine($"INTERFACE=\"{_config.Interface}\"");
        sb.AppendLine($"IFB_DEVICE=\"ifb{_config.Interface}\"");
        sb.AppendLine($"PING_HOST=\"{_config.PingHost}\"");
        sb.AppendLine($"BASELINE_LATENCY={Inv(_config.BaselineLatency)}");
        sb.AppendLine($"LATENCY_THRESHOLD={Inv(_config.LatencyThreshold)}");
        sb.AppendLine($"LATENCY_DECREASE={Inv(_config.LatencyDecrease)}");
        sb.AppendLine($"LATENCY_INCREASE={Inv(_config.LatencyIncrease)}");
        sb.AppendLine($"MIN_DOWNLOAD_SPEED=\"{_config.MinDownloadSpeed}\"");
        sb.AppendLine($"ABSOLUTE_MAX_DOWNLOAD_SPEED=\"{_config.AbsoluteMaxDownloadSpeed}\"");
        sb.AppendLine($"MAX_DOWNLOAD_SPEED_CONFIG=\"{_config.MaxDownloadSpeed}\"");
        sb.AppendLine($"RESULT_FILE=\"/data/sqm/{_name}-result.txt\"");
        sb.AppendLine($"LOG_FILE=\"/var/log/sqm-{_name}.log\"");
        sb.AppendLine();

        // Baseline data
        sb.AppendLine("# Baseline speeds by day of week (0=Mon, 6=Sun) and hour");
        sb.AppendLine("declare -A BASELINE");
        foreach (var (key, value) in baseline.OrderBy(b => b.Key))
        {
            sb.AppendLine($"BASELINE[{key}]=\"{value}\"");
        }
        sb.AppendLine();

        // Check for result file
        sb.AppendLine("# Check for speedtest result");
        sb.AppendLine("if [ ! -f \"$RESULT_FILE\" ]; then");
        sb.AppendLine("    echo \"[$(date)] No speedtest result file, skipping\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Parse and validate speed test result to prevent tc breakage
        sb.AppendLine("# Parse speedtest result with validation");
        sb.AppendLine("SPEEDTEST_SPEED=$(cat \"$RESULT_FILE\" | awk '{print $4}')");
        sb.AppendLine();
        sb.AppendLine("# Validate speedtest result is a valid positive number");
        sb.AppendLine("# This prevents tc breakage from invalid/blank/non-numeric values");
        sb.AppendLine("if [ -z \"$SPEEDTEST_SPEED\" ]; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: Speedtest result is empty, skipping ping adjustment\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Check if value is numeric (integer or decimal)");
        sb.AppendLine("if ! echo \"$SPEEDTEST_SPEED\" | grep -qE '^[0-9]+\\.?[0-9]*$'; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: Speedtest result '$SPEEDTEST_SPEED' is not a valid number, skipping ping adjustment\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Check if value is reasonable (> 0 and < 100000 Mbps)");
        sb.AppendLine("if (( $(echo \"$SPEEDTEST_SPEED <= 0\" | bc -l) )) || (( $(echo \"$SPEEDTEST_SPEED > 100000\" | bc -l) )); then");
        sb.AppendLine("    echo \"[$(date)] ERROR: Speedtest result '$SPEEDTEST_SPEED' Mbps is out of valid range (0-100000), skipping ping adjustment\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Verify IFB device exists (created by UniFi Smart Queues)
        sb.AppendLine("# Verify IFB device exists (created by UniFi Smart Queues)");
        sb.AppendLine("if ! ip link show \"$IFB_DEVICE\" &>/dev/null; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: IFB device $IFB_DEVICE does not exist. Smart Queues may not be enabled in UniFi Network settings.\" >> $LOG_FILE");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Baseline lookup for ping
        sb.AppendLine(GetBaselineBlendingLogicForPing());
        sb.AppendLine();

        // Measure latency with validation
        sb.AppendLine("# Measure latency");
        sb.AppendLine($"latency=$(ping -I $INTERFACE -c 20 -i 0.25 -q \"$PING_HOST\" 2>/dev/null | tail -n 1 | awk -F '/' '{{print $5}}')");
        sb.AppendLine();
        sb.AppendLine("# Validate latency result");
        sb.AppendLine("if [ -z \"$latency\" ]; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: Ping to $PING_HOST failed (no response), skipping adjustment\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Check if latency is a valid number");
        sb.AppendLine("if ! echo \"$latency\" | grep -qE '^[0-9]+\\.?[0-9]*$'; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: Ping latency '$latency' is not a valid number, skipping adjustment\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("deviation_count=$(echo \"($latency - $BASELINE_LATENCY) / $LATENCY_THRESHOLD\" | bc)");
        sb.AppendLine();

        // Latency adjustment logic
        sb.AppendLine(GetLatencyAdjustmentLogic());
        sb.AppendLine();

        // Apply limits
        sb.AppendLine("# Apply limits");
        sb.AppendLine("max_adjusted_rate=$(echo \"$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.95\" | bc)");
        sb.AppendLine("if (( $(echo \"$new_rate > $max_adjusted_rate\" | bc) )); then");
        sb.AppendLine("    new_rate=$max_adjusted_rate");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("new_rate=$(echo \"scale=1; $new_rate / 1\" | bc)");
        sb.AppendLine();
        sb.AppendLine("if (( $(echo \"$new_rate > $MAX_DOWNLOAD_SPEED_CONFIG\" | bc) )); then");
        sb.AppendLine("    new_rate=$MAX_DOWNLOAD_SPEED_CONFIG");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Final validation before applying tc changes
        sb.AppendLine("# Final validation before applying tc changes");
        sb.AppendLine("if [ -z \"$new_rate\" ]; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: Calculated rate is empty, skipping tc update\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Ensure new_rate is a valid positive number");
        sb.AppendLine("if ! echo \"$new_rate\" | grep -qE '^[0-9]+\\.?[0-9]*$'; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: Calculated rate '$new_rate' is not a valid number, skipping tc update\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Convert to integer for tc (tc doesn't accept decimals in Mbit)");
        sb.AppendLine("new_rate_int=$(printf \"%.0f\" \"$new_rate\")");
        sb.AppendLine("if [ \"$new_rate_int\" -le 0 ] 2>/dev/null; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: Calculated rate '$new_rate_int' Mbps is <= 0, skipping tc update\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();

        // TC update function and apply
        sb.AppendLine(GetTcUpdateFunction());
        sb.AppendLine();
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $new_rate_int");
        sb.AppendLine();
        sb.AppendLine("echo \"[$(date)] Ping adjusted to $new_rate_int Mbps (latency: ${latency}ms)\" >> $LOG_FILE");

        return sb.ToString();
    }

    /// <summary>
    /// Get TC update function (common to both scripts)
    /// </summary>
    private string GetTcUpdateFunction()
    {
        return @"# Function to update all TC classes on a device
update_all_tc_classes() {
    local device=$1
    local new_rate=$2

    # Update the root class 1:1 with rate and ceil
    tc class change dev $device parent 1: classid 1:1 htb rate ${new_rate}Mbit ceil ${new_rate}Mbit burst 1500b cburst 1500b

    # Get all child classes and update their ceil values (skip classes with rate > 64bit)
    tc class show dev $device | grep ""parent 1:1"" | while read line; do
        classid=$(echo ""$line"" | grep -o ""class htb [0-9:]*"" | awk '{print $3}')
        prio=$(echo ""$line"" | grep -o ""prio [0-9]*"" | awk '{print $2}')
        rate=$(echo ""$line"" | grep -o ""rate [0-9]*[a-zA-Z]*"" | awk '{print $2}')

        # Skip classes with rate > 64bit (UniFi-configured classes)
        if [ ""$rate"" != ""64bit"" ]; then
            continue
        fi

        if [ -n ""$classid"" ]; then
            if [ -n ""$prio"" ]; then
                tc class change dev $device parent 1:1 classid $classid htb rate 64bit ceil ${new_rate}Mbit burst 1500b cburst 1500b prio $prio
            else
                tc class change dev $device parent 1:1 classid $classid htb rate 64bit ceil ${new_rate}Mbit burst 1500b cburst 1500b
            fi
        fi
    done
}";
    }

    /// <summary>
    /// Get baseline blending logic for speedtest script
    /// </summary>
    private string GetBaselineBlendingLogic()
    {
        var withinBaseline = Inv(_config.BlendingWeightWithin);
        var withinMeasured = Inv(1.0 - _config.BlendingWeightWithin);
        var belowBaseline = Inv(_config.BlendingWeightBelow);
        var belowMeasured = Inv(1.0 - _config.BlendingWeightBelow);

        var withinRatio = $"{(int)(_config.BlendingWeightWithin * 100)}/{(int)((1.0 - _config.BlendingWeightWithin) * 100)}";
        var belowRatio = $"{(int)(_config.BlendingWeightBelow * 100)}/{(int)((1.0 - _config.BlendingWeightBelow) * 100)}";

        return $@"# Baseline blending
current_day=$(date +%u)
current_day=$((current_day - 1))
current_hour=$(date +%H | sed 's/^0//')
lookup_key=""${{current_day}}_${{current_hour}}""

baseline_speed=${{BASELINE[$lookup_key]}}

if [ -n ""$baseline_speed"" ]; then
    threshold=$(echo ""scale=0; $baseline_speed * 0.9 / 1"" | bc)

    if [ ""$download_speed_mbps"" -ge ""$threshold"" ]; then
        # Within 10%: blend {withinRatio}
        blended_speed=$(echo ""scale=0; ($baseline_speed * {withinBaseline} + $download_speed_mbps * {withinMeasured}) / 1"" | bc)
    else
        # Below 10%: favor baseline {belowRatio}
        blended_speed=$(echo ""scale=0; ($baseline_speed * {belowBaseline} + $download_speed_mbps * {belowMeasured}) / 1"" | bc)
    fi

    download_speed_mbps=$(echo ""scale=0; $blended_speed * $DOWNLOAD_SPEED_MULTIPLIER / 1"" | bc)
else
    download_speed_mbps=$(echo ""scale=0; $download_speed_mbps * $DOWNLOAD_SPEED_MULTIPLIER / 1"" | bc)
fi";
    }

    /// <summary>
    /// Get baseline blending logic for ping script
    /// </summary>
    private string GetBaselineBlendingLogicForPing()
    {
        var baselineWeight = Inv(_config.BlendingWeightWithin);
        var measuredWeight = Inv(1.0 - _config.BlendingWeightWithin);
        var overheadMultiplier = Inv(_config.OverheadMultiplier);

        return $@"# Baseline blending for ping
current_day=$(date +%u)
current_day=$((current_day - 1))
current_hour=$(date +%H | sed 's/^0//')
lookup_key=""${{current_day}}_${{current_hour}}""

baseline_speed=${{BASELINE[$lookup_key]}}

if [ -n ""$baseline_speed"" ]; then
    baseline_with_overhead=$(echo ""scale=0; $baseline_speed * {overheadMultiplier} / 1"" | bc)
    if [ ""$baseline_with_overhead"" -gt ""$MAX_DOWNLOAD_SPEED_CONFIG"" ]; then
        baseline_with_overhead=$MAX_DOWNLOAD_SPEED_CONFIG
    fi
    MAX_DOWNLOAD_SPEED=$(echo ""scale=0; ($baseline_with_overhead * {baselineWeight} + $SPEEDTEST_SPEED * {measuredWeight}) / 1"" | bc)
else
    MAX_DOWNLOAD_SPEED=$SPEEDTEST_SPEED
fi";
    }

    /// <summary>
    /// Get latency adjustment logic for ping script
    /// </summary>
    private string GetLatencyAdjustmentLogic()
    {
        return @"# Latency-based adjustment
if (( $(echo ""$latency >= $BASELINE_LATENCY + $LATENCY_THRESHOLD"" | bc -l) )); then
    # High latency: decrease rate
    decrease_multiplier=$(echo ""$LATENCY_DECREASE^$deviation_count"" | bc -l)
    new_rate=$(echo ""$MAX_DOWNLOAD_SPEED * $decrease_multiplier"" | bc)
    if (( $(echo ""$new_rate < $MIN_DOWNLOAD_SPEED"" | bc) )); then
        new_rate=$MIN_DOWNLOAD_SPEED
    fi

elif (( $(echo ""$latency < $BASELINE_LATENCY - 0.4"" | bc -l) )); then
    # Low latency: can increase
    lower_bound=$(echo ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.92"" | bc)
    mid_bound=$(echo ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.94"" | bc)
    if (( $(echo ""$MAX_DOWNLOAD_SPEED < $lower_bound"" | bc -l) )); then
        new_rate=$(echo ""$MAX_DOWNLOAD_SPEED * $LATENCY_INCREASE * $LATENCY_INCREASE"" | bc -l)
    elif (( $(echo ""$MAX_DOWNLOAD_SPEED < $mid_bound"" | bc -l) )); then
        new_rate=$mid_bound
    else
        new_rate=$MAX_DOWNLOAD_SPEED
    fi

else
    # Normal latency
    lower_bound=$(echo ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.9"" | bc)
    mid_bound=$(echo ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.92"" | bc)
    latency_diff=$(echo ""$latency - $BASELINE_LATENCY"" | bc -l)
    latency_normal=$(echo ""$latency_diff <= 0.3"" | bc -l)

    if (( $(echo ""$MAX_DOWNLOAD_SPEED < $lower_bound"" | bc -l) )) && (( latency_normal == 1 )); then
        new_rate=$(echo ""$MAX_DOWNLOAD_SPEED * $LATENCY_INCREASE"" | bc)
    elif (( $(echo ""$MAX_DOWNLOAD_SPEED < $mid_bound"" | bc -l) )) && (( latency_normal == 1 )); then
        new_rate=$mid_bound
    else
        new_rate=$MAX_DOWNLOAD_SPEED
    fi
fi";
    }
}
