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
        sb.AppendLine($"# Schedule speedtest calibration {_initialDelaySeconds} seconds after boot");
        sb.AppendLine($"echo \"[$(date)] Scheduling initial SQM calibration in {_initialDelaySeconds} seconds...\" >> $LOG_FILE");
        sb.AppendLine($"systemd-run --on-active={_initialDelaySeconds}sec --timer-property=AccuracySec=1s \\");
        sb.AppendLine("  --setenv=PATH=\"$PATH\" \\");
        sb.AppendLine("  --setenv=HOME=/root \\");
        sb.AppendLine("  \"$SPEEDTEST_SCRIPT\"");
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
        sb.AppendLine($"UPLOAD_SPEED=\"{_config.NominalUploadSpeed}\"");
        sb.AppendLine($"SHAPE_UPLOAD={(_config.ShapeUpload ? "1" : "0")}");
        sb.AppendLine($"DOWNLOAD_SPEED_MULTIPLIER=\"{Inv(_config.OverheadMultiplier)}\"");
        sb.AppendLine($"SAFETY_CAP=\"{Inv(_config.SafetyCapPercent)}\"");
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
        sb.AppendLine("# Upstream: shape rate if enabled, otherwise just tune performance params");
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine("    update_all_tc_classes $INTERFACE $UPLOAD_SPEED");
        sb.AppendLine("else");
        sb.AppendLine("    tune_tc_performance $INTERFACE");
        sb.AppendLine("fi");
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

        // Apply safety cap
        sb.AppendLine("# Apply safety cap");
        sb.AppendLine("max_adjusted_rate=$(echo \"$MAX_DOWNLOAD_SPEED * $SAFETY_CAP / 1\" | bc)");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps > max_adjusted_rate ? max_adjusted_rate : download_speed_mbps))");
        sb.AppendLine();

        // Save result and apply
        sb.AppendLine("# Save result for ping script");
        sb.AppendLine("echo \"Measured download speed: $download_speed_mbps Mbps\" > \"$RESULT_FILE\"");
        sb.AppendLine();
        sb.AppendLine("# Apply TC classes (downstream and upstream)");
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $download_speed_mbps");
        sb.AppendLine("# Upstream: shape rate if enabled, otherwise just tune performance params");
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine("    update_all_tc_classes $INTERFACE $UPLOAD_SPEED");
        sb.AppendLine("else");
        sb.AppendLine("    tune_tc_performance $INTERFACE");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine("    echo \"[$(date)] Adjusted to $download_speed_mbps Mbps (down), $UPLOAD_SPEED Mbps (up)\" >> $LOG_FILE");
        sb.AppendLine("else");
        sb.AppendLine("    echo \"[$(date)] Adjusted to $download_speed_mbps Mbps (down), upstream perf-tuned\" >> $LOG_FILE");
        sb.AppendLine("fi");

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
        sb.AppendLine($"UPLOAD_SPEED=\"{_config.NominalUploadSpeed}\"");
        sb.AppendLine($"SHAPE_UPLOAD={(_config.ShapeUpload ? "1" : "0")}");
        sb.AppendLine($"SAFETY_CAP=\"{Inv(_config.SafetyCapPercent)}\"");
        sb.AppendLine($"NOMINAL_SPEED=\"{_config.NominalDownloadSpeed}\"");
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

        // Apply safety cap to MAX_DOWNLOAD_SPEED BEFORE latency adjustment.
        // This sets the schedule-derived ceiling as the starting point, then latency
        // can freely decrease below it. Without this, the cap creates a dead zone where
        // mild latency spikes are detected but produce no visible rate change.
        var useBaselineRatio = _config.ConnectionType is ConnectionType.Gpon or ConnectionType.XgsPon;
        if (useBaselineRatio)
        {
            sb.AppendLine("# Apply baseline-proportional safety cap before latency adjustment (fiber)");
            sb.AppendLine("if [ -n \"$baseline_speed\" ] && [ \"$NOMINAL_SPEED\" -gt 0 ]; then");
            sb.AppendLine("    baseline_ratio=$(echo \"scale=4; $baseline_speed / $NOMINAL_SPEED\" | bc)");
            sb.AppendLine("    max_adjusted_rate=$(echo \"scale=0; $ABSOLUTE_MAX_DOWNLOAD_SPEED * $SAFETY_CAP * $baseline_ratio / 1\" | bc)");
            sb.AppendLine("else");
            sb.AppendLine("    max_adjusted_rate=$(echo \"$ABSOLUTE_MAX_DOWNLOAD_SPEED * $SAFETY_CAP\" | bc)");
            sb.AppendLine("fi");
        }
        else
        {
            sb.AppendLine("# Apply flat safety cap before latency adjustment");
            sb.AppendLine("max_adjusted_rate=$(echo \"$ABSOLUTE_MAX_DOWNLOAD_SPEED * $SAFETY_CAP\" | bc)");
        }
        sb.AppendLine("if (( $(echo \"$MAX_DOWNLOAD_SPEED > $max_adjusted_rate\" | bc) )); then");
        sb.AppendLine("    MAX_DOWNLOAD_SPEED=$(echo \"scale=0; $max_adjusted_rate / 1\" | bc)");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Measure latency with validation
        sb.AppendLine("# Measure latency");
        sb.AppendLine($"latency=$(ping -I $INTERFACE -c 10 -i 0.5 -q \"$PING_HOST\" 2>/dev/null | tail -n 1 | awk -F '/' '{{print $5}}')");
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

        // Latency adjustment logic (operates on capped MAX_DOWNLOAD_SPEED, can decrease freely)
        sb.AppendLine(GetLatencyAdjustmentLogic());
        sb.AppendLine();

        // Post-latency ceiling: prevent increase branch from exceeding schedule cap
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
        sb.AppendLine("# Upstream: shape rate if enabled, otherwise just tune performance params");
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine("    update_all_tc_classes $INTERFACE $UPLOAD_SPEED");
        sb.AppendLine("else");
        sb.AppendLine("    tune_tc_performance $INTERFACE");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine("    echo \"[$(date)] Ping adjusted to $new_rate_int Mbps (down), $UPLOAD_SPEED Mbps (up) (latency: ${latency}ms)\" >> $LOG_FILE");
        sb.AppendLine("else");
        sb.AppendLine("    echo \"[$(date)] Ping adjusted to $new_rate_int Mbps (down), upstream perf-tuned (latency: ${latency}ms)\" >> $LOG_FILE");
        sb.AppendLine("fi");

        return sb.ToString();
    }

    /// <summary>
    /// Get TC update function (common to both scripts)
    /// </summary>
    private string GetTcUpdateFunction()
    {
        return @"# Calculate burst size scaled to rate (prevents frame drops at high speeds)
# Config E testing showed 5KB is optimal at gig speeds: eliminates downstream drop_overmemory
# without the bufferbloat regression seen at 8KB+ (which feeds fq_codel faster than it can react)
# Scale: 5 bytes per Mbps, floor 1500b (stock), cap 5000b
calc_burst() {
    local rate_mbps=$1
    local burst=$((rate_mbps * 5))
    [ ""$burst"" -lt 1500 ] && burst=1500
    [ ""$burst"" -gt 5000 ] && burst=5000
    echo ""$burst""
}

# Calculate fq_codel memory_limit scaled to rate (prevents drop_overmemory at high speeds)
# Testing showed 8MB is needed for real-world multi-stream workloads (Steam, backups):
#   - 4MB (stock): ~1400 drop_overmemory per bufferbloat test, ~300/sec during Steam downloads
#   - 6MB: eliminates most downstream drop_overmemory on synthetic tests, but still hits
#     memory wall during heavy multi-stream downloads (Steam: 5.6/5.7MB = 99% full)
#   - 8MB: zero drop_overmemory during Steam downloads, memory stays at ~30-40% utilization
#     Bufferbloat test shows ~5ms regression vs 6MB, but real-world latency is at idle levels
#     because the extra headroom lets fq_codel do proper AQM instead of panic-dropping
# Combined with 95% safety cap on fiber (950 Mbps vs 980), htb has room to shape properly
# Piecewise scaling:
#   0-300 Mbps: 4MB floor (stock is fine, no GSO pressure at these rates)
#   300-750 Mbps: linear ramp from 4MB to 8MB
#   750+ Mbps: 8MB cap (needed for multi-stream gig downloads regardless of exact rate)
# This avoids a cliff at the threshold while ensuring gig connections always get 8MB
calc_fq_mem() {
    local rate_mbps=$1
    local mem
    if [ ""$rate_mbps"" -ge 750 ]; then
        mem=8388608
    elif [ ""$rate_mbps"" -le 300 ]; then
        mem=4194304
    else
        # Linear ramp: 4MB at 300 Mbps to 8MB at 750 Mbps
        # slope = (8388608 - 4194304) / (750 - 300) = 9320 bytes per Mbps
        mem=$(( 4194304 + (rate_mbps - 300) * 9320 ))
    fi
    echo ""$mem""
}

# Calculate fq_codel packet limit scaled to rate
# Stock 2000p is fine for all tested rates — not the binding constraint
calc_fq_limit() {
    local rate_mbps=$1
    local limit=2000
    echo ""$limit""
}

# Function to update all TC classes on a device
update_all_tc_classes() {
    local device=$1
    local new_rate=$2
    local burst=$(calc_burst $new_rate)
    local fq_mem=$(calc_fq_mem $new_rate)
    local fq_limit=$(calc_fq_limit $new_rate)

    # Update the root class 1:1 with rate and ceil
    tc class change dev $device parent 1: classid 1:1 htb rate ${new_rate}Mbit ceil ${new_rate}Mbit burst ${burst}b cburst ${burst}b

    # Get all child classes and update their ceil values (skip classes with rate > 64bit)
    # Note: tc uses hex for class IDs >= 10 (e.g., 1:a, 1:b), so include a-f in patterns
    tc class show dev $device | grep -E ""parent 1:1( |$)"" | while read line; do
        classid=$(echo ""$line"" | grep -o ""class htb [0-9a-f:]*"" | awk '{print $3}')
        prio=$(echo ""$line"" | grep -o ""prio [0-9]*"" | awk '{print $2}')
        rate=$(echo ""$line"" | grep -o ""rate [0-9]*[a-zA-Z]*"" | awk '{print $2}')

        # Skip classes with a real guaranteed rate (UniFi-configured classes).
        # Match 64bit (stock UniFi) and 100Kbit (after our update) as best-effort markers.
        if [ ""$rate"" != ""64bit"" ] && [ ""$rate"" != ""100Kbit"" ]; then
            continue
        fi

        if [ -n ""$classid"" ]; then
            # Tune the fq_codel leaf qdisc for this class (if present)
            # tc qdisc show format: ""qdisc fq_codel 8004: parent 1:4 ...""
            #   field 1=qdisc, field 2=type, field 3=handle
            local qdisc_line=$(tc qdisc show dev $device | grep -E ""parent ${classid}( |$)"")
            local qdisc_type=$(echo ""$qdisc_line"" | awk '{print $2}')
            local leaf_qdisc=$(echo ""$qdisc_line"" | awk '{print $3}')
            if [ ""$qdisc_type"" = ""fq_codel"" ] && [ -n ""$leaf_qdisc"" ]; then
                tc qdisc change dev $device parent $classid handle $leaf_qdisc fq_codel limit $fq_limit memory_limit $fq_mem target 5ms interval 100ms ecn
            fi

            if [ -n ""$prio"" ]; then
                tc class change dev $device parent 1:1 classid $classid htb rate 100kbit ceil ${new_rate}Mbit burst ${burst}b cburst ${burst}b prio $prio
            else
                tc class change dev $device parent 1:1 classid $classid htb rate 100kbit ceil ${new_rate}Mbit burst ${burst}b cburst ${burst}b
            fi
        fi
    done
}

# Tune performance params (burst, fq_codel) on a device without changing rates
# Reads the current root rate and applies scaled burst/memory params
tune_tc_performance() {
    local device=$1

    # Read current root rate string from 1:1 class (e.g., ""1Gbit"", ""980Mbit"", ""29Mbit"")
    local rate_str=$(tc class show dev $device | grep ""class htb 1:1 root"" | grep -o ""rate [0-9]*[a-zA-Z]*"" | awk '{print $2}')
    if [ -z ""$rate_str"" ]; then
        return
    fi

    # Convert to Mbps based on unit suffix
    local current_rate
    case ""$rate_str"" in
        *Gbit)  current_rate=$(echo ""$rate_str"" | sed 's/Gbit//') ; current_rate=$((current_rate * 1000)) ;;
        *Mbit)  current_rate=$(echo ""$rate_str"" | sed 's/Mbit//') ;;
        *Kbit)  current_rate=$(echo ""$rate_str"" | sed 's/Kbit//') ; current_rate=$(( (current_rate + 500) / 1000 )) ;;
        *bit)   current_rate=$(echo ""$rate_str"" | sed 's/bit//') ; current_rate=$(( (current_rate + 500000) / 1000000 )) ;;
        *)      return ;;
    esac

    if [ ""$current_rate"" -le 0 ] 2>/dev/null; then
        return
    fi

    update_all_tc_classes $device $current_rate
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
