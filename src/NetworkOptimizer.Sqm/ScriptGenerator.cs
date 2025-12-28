using System.Text;
using NetworkOptimizer.Sqm.Models;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Generates shell scripts for SQM deployment on UniFi devices
/// </summary>
public class ScriptGenerator
{
    private readonly SqmConfiguration _config;
    private readonly string _templateDirectory;

    public ScriptGenerator(SqmConfiguration config, string? templateDirectory = null)
    {
        _config = config;
        _templateDirectory = templateDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates");
    }

    /// <summary>
    /// Generate all scripts required for SQM deployment
    /// </summary>
    public Dictionary<string, string> GenerateAllScripts(Dictionary<string, string> baseline)
    {
        var scripts = new Dictionary<string, string>
        {
            ["20-sqm-speedtest-setup.sh"] = GenerateSpeedtestSetupScript(baseline),
            ["21-sqm-ping-setup.sh"] = GeneratePingSetupScript(baseline),
            ["sqm-speedtest-adjust.sh"] = GenerateSpeedtestAdjustScript(baseline),
            ["sqm-ping-adjust.sh"] = GeneratePingAdjustScript(baseline),
            ["install.sh"] = GenerateInstallScript()
        };

        // Add metrics collector if InfluxDB is configured
        if (!string.IsNullOrWhiteSpace(_config.InfluxDbEndpoint))
        {
            scripts["sqm-metrics-collector.sh"] = GenerateMetricsCollectorScript();
        }

        return scripts;
    }

    /// <summary>
    /// Generate speedtest setup script (boot script)
    /// </summary>
    public string GenerateSpeedtestSetupScript(Dictionary<string, string> baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine("# Path for the actual speedtest script");
        sb.AppendLine("SPEEDTEST_SCRIPT_PATH=\"/data/sqm-speedtest-adjust.sh\"");
        sb.AppendLine();
        sb.AppendLine("# If speedtest command does not exist, it is likely the UniFi version, so remove it");
        sb.AppendLine("if ! which speedtest > /dev/null; then");
        sb.AppendLine("    echo \"UniFi's speedtest found. Uninstalling speedtest...\"");
        sb.AppendLine("    apt-get remove -y speedtest");
        sb.AppendLine("    echo \"Installing official speedtest by Ookla...\"");
        sb.AppendLine();
        sb.AppendLine("    # Install the official Speedtest by Ookla");
        sb.AppendLine("    curl -s https://packagecloud.io/install/repositories/ookla/speedtest-cli/script.deb.sh | bash");
        sb.AppendLine("    apt-get install -y speedtest");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Install bc if not already installed");
        sb.AppendLine("if ! which bc > /dev/null; then");
        sb.AppendLine("    echo \"Installing bc...\"");
        sb.AppendLine("    apt-get install -y bc");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Copy the speedtest adjust script");
        sb.AppendLine("if [ ! -f \"$SPEEDTEST_SCRIPT_PATH\" ]; then");
        sb.AppendLine("    echo \"Copying speedtest adjust script...\"");
        sb.AppendLine("    cp /data/scripts/sqm-speedtest-adjust.sh \"$SPEEDTEST_SCRIPT_PATH\"");
        sb.AppendLine("    chmod +x \"$SPEEDTEST_SCRIPT_PATH\"");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Set cron jobs for speedtest");

        foreach (var schedule in _config.SpeedtestSchedule)
        {
            sb.AppendLine($"CRON_JOB=\"{schedule} export PATH=\\\"$PATH\\\"; export HOME=/root; $SPEEDTEST_SCRIPT_PATH\"");
        }

        sb.AppendLine();
        sb.AppendLine("# Check if the cron jobs already exist before adding");
        sb.AppendLine("if ! crontab -l | grep -Fq \"$SPEEDTEST_SCRIPT_PATH\"; then");

        for (int i = 0; i < _config.SpeedtestSchedule.Count; i++)
        {
            if (i == 0)
            {
                sb.Append("    (crontab -l 2>/dev/null");
            }
            sb.Append($"; echo \"{_config.SpeedtestSchedule[i]} export PATH=\\\"$PATH\\\"; export HOME=/root; $SPEEDTEST_SCRIPT_PATH\"");
        }
        sb.AppendLine(") | crontab -");

        sb.AppendLine("    echo \"Cron jobs set for speedtest.\"");
        sb.AppendLine("else");
        sb.AppendLine("    echo \"Cron jobs already exist. Skipping cron job setup.\"");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("echo \"Script setup complete.\"");
        sb.AppendLine();
        sb.AppendLine("# Schedule the baseline run to happen after system start");
        sb.AppendLine("echo \"Scheduling SQM calibration to run once in 30 seconds...\"");
        sb.AppendLine("systemd-run --on-active=30sec --timer-property=AccuracySec=1s \\");
        sb.AppendLine("  --setenv=PATH=\"$(echo $PATH)\" \\");
        sb.AppendLine("  --setenv=HOME=/root \\");
        sb.AppendLine("  \"$SPEEDTEST_SCRIPT_PATH\"");

        return sb.ToString();
    }

    /// <summary>
    /// Generate ping setup script (boot script)
    /// </summary>
    public string GeneratePingSetupScript(Dictionary<string, string> baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine("# Paths and constants");
        sb.AppendLine("PING_ADJUST_SCRIPT=\"/data/sqm-ping-adjust.sh\"");
        sb.AppendLine();
        sb.AppendLine("# Copy the ping adjustment script");
        sb.AppendLine("if [ ! -f \"$PING_ADJUST_SCRIPT\" ]; then");
        sb.AppendLine("    echo \"Copying ping adjustment script...\"");
        sb.AppendLine("    cp /data/scripts/sqm-ping-adjust.sh \"$PING_ADJUST_SCRIPT\"");
        sb.AppendLine("    chmod +x \"$PING_ADJUST_SCRIPT\"");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine($"# Create a cron job to run the ping adjustment script every {_config.PingAdjustmentInterval} minutes");
        sb.AppendLine($"CRON_JOB=\"*/{_config.PingAdjustmentInterval} * * * * export PATH=\\\"$PATH\\\"; export HOME=/root; \\");

        // Exclude speedtest times
        sb.Append("if [");
        for (int i = 0; i < _config.SpeedtestSchedule.Count; i++)
        {
            var parts = _config.SpeedtestSchedule[i].Split(' ');
            if (parts.Length >= 2)
            {
                var minute = parts[0];
                var hour = parts[1];
                sb.Append($" \\\"\\$(date +\\%H:\\%M)\\\" != \\\"{hour.PadLeft(2, '0')}:{minute.PadLeft(2, '0')}\\\"");
                if (i < _config.SpeedtestSchedule.Count - 1)
                {
                    sb.Append(" ] && [");
                }
            }
        }
        sb.AppendLine(" ]; then $PING_ADJUST_SCRIPT; fi\"");
        sb.AppendLine();
        sb.AppendLine("# Check if the cron job already exists before adding");
        sb.AppendLine("if ! crontab -l | grep -Fq \"$PING_ADJUST_SCRIPT\"; then");
        sb.AppendLine("    (crontab -l 2>/dev/null; echo -e \"$CRON_JOB\") | crontab -");
        sb.AppendLine($"    echo \"Cron job set to run the ping adjustment script every {_config.PingAdjustmentInterval} minutes.\"");
        sb.AppendLine("else");
        sb.AppendLine("    echo \"Cron job already exists. Skipping cron job setup.\"");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("echo \"Ping-based adjustment script created and cron job scheduled.\"");

        return sb.ToString();
    }

    /// <summary>
    /// Generate speedtest adjustment script
    /// </summary>
    public string GenerateSpeedtestAdjustScript(Dictionary<string, string> baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine("# Check if 'speedtest' is installed");
        sb.AppendLine("if ! which speedtest > /dev/null; then");
        sb.AppendLine("    echo \"speedtest command not found. Exiting...\"");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine($"MAX_DOWNLOAD_SPEED=\"{_config.MaxDownloadSpeed}\" # SQM Mbps to set before speed testing (also ceiling for adjusted speed)");
        sb.AppendLine($"MIN_DOWNLOAD_SPEED=\"{_config.MinDownloadSpeed}\" # Mbps floor for measured speed");
        sb.AppendLine($"DOWNLOAD_SPEED_MULTIPLIER=\"{_config.OverheadMultiplier}\" # Multiply averaged speed by overhead");
        sb.AppendLine("SPEEDTEST_RESULTS_FILE=\"/data/sqm-speedtest-result.txt\"");
        sb.AppendLine($"INTERFACE=\"{_config.Interface}\"");
        sb.AppendLine($"IFB_DEVICE=\"ifb{_config.Interface}\"");
        sb.AppendLine();
        sb.AppendLine("# Baseline speeds by day of week (0=Mon, 6=Sun) and hour");
        sb.AppendLine("# Format: BASELINE[day_hour]=median_speed");
        sb.AppendLine("declare -A BASELINE");

        // Generate baseline array
        foreach (var (key, value) in baseline.OrderBy(b => b.Key))
        {
            sb.AppendLine($"BASELINE[{key}]=\"{value}\"");
        }

        sb.AppendLine();
        sb.AppendLine($"echo \"[$(date)] Adjusting speedtest-based rate on {_config.Interface}...\" >> /var/log/sqm-speedtest-adjust.log");
        sb.AppendLine();

        // Add the update_all_tc_classes function
        sb.AppendLine(GetTcUpdateFunction());
        sb.AppendLine();

        sb.AppendLine("# Show the current tc class configuration before applying changes");
        sb.AppendLine("echo \"Current tc class configuration:\"");
        sb.AppendLine($"tc class show dev $IFB_DEVICE | grep \"class htb\"");
        sb.AppendLine();
        sb.AppendLine("# Set SQM to max possible download speed before speed testing");
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $MAX_DOWNLOAD_SPEED");
        sb.AppendLine();
        sb.AppendLine("# Run the speedtest and accept the license");
        sb.AppendLine($"speedtest_output=$(speedtest --accept-license --format=json --interface={_config.Interface})");
        sb.AppendLine();
        sb.AppendLine("# Parse the JSON output to get the download speed in bytes/sec");
        sb.AppendLine("download_speed_bytes_per_sec=$(echo \"$speedtest_output\" | jq .download.bandwidth)");
        sb.AppendLine();
        sb.AppendLine("# Convert bytes/sec to Mbps");
        sb.AppendLine("download_speed_mbps=$(echo \"scale=0; $download_speed_bytes_per_sec * 8 / 1000000\" | bc)");
        sb.AppendLine();
        sb.AppendLine("echo \"Download speed measured: $download_speed_mbps Mbps\"");
        sb.AppendLine($"echo \"[$(date)] Download speed measured on {_config.Interface}: $download_speed_mbps Mbps\" >> /var/log/sqm-speedtest-adjust.log");
        sb.AppendLine();
        sb.AppendLine("# Set minimum download speed to MIN_DOWNLOAD_SPEED using ternary logic");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps < $MIN_DOWNLOAD_SPEED ? $MIN_DOWNLOAD_SPEED : download_speed_mbps))");
        sb.AppendLine("echo \"Download speed after floor: $download_speed_mbps Mbps\"");
        sb.AppendLine();

        // Baseline lookup and blending logic
        sb.AppendLine(GetBaselineBlendingLogic());
        sb.AppendLine();

        sb.AppendLine("# Cap at MAX_DOWNLOAD_SPEED");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps > $MAX_DOWNLOAD_SPEED ? $MAX_DOWNLOAD_SPEED : download_speed_mbps))");
        sb.AppendLine();
        sb.AppendLine("# Apply 95% cap (same as ping script)");
        sb.AppendLine("max_adjusted_rate=$(echo \"$MAX_DOWNLOAD_SPEED * 0.95 / 1\" | bc)");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps > max_adjusted_rate ? max_adjusted_rate : download_speed_mbps))");
        sb.AppendLine("echo \"Final speed after 95% cap: $download_speed_mbps Mbps\"");
        sb.AppendLine("echo \"Measured download speed: $download_speed_mbps Mbps\" > \"$SPEEDTEST_RESULTS_FILE\"");
        sb.AppendLine();
        sb.AppendLine("# Adjust all TC classes with the new speed");
        sb.AppendLine("echo \"Adjusting TC classes for adjusted download speed: $download_speed_mbps Mbps...\"");
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $download_speed_mbps");
        sb.AppendLine();
        sb.AppendLine($"echo \"[$(date)] Adjusted speedtest-based rate to $download_speed_mbps Mbps on {_config.Interface}\" >> /var/log/sqm-speedtest-adjust.log");
        sb.AppendLine();
        sb.AppendLine("# Show the updated tc class configuration");
        sb.AppendLine("echo \"Updated tc class configuration:\"");
        sb.AppendLine("tc class show dev $IFB_DEVICE | grep \"class htb\"");

        return sb.ToString();
    }

    /// <summary>
    /// Generate ping adjustment script
    /// </summary>
    public string GeneratePingAdjustScript(Dictionary<string, string> baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine($"echo \"[$(date)] SQM Ping Script invoked on {_config.Interface}\" >> /var/log/sqm-ping-adjust.log");
        sb.AppendLine();
        sb.AppendLine("# Paths and constants");
        sb.AppendLine("SPEEDTEST_RESULTS_FILE=\"/data/sqm-speedtest-result.txt\"");
        sb.AppendLine($"INTERFACE=\"{_config.Interface}\"");
        sb.AppendLine($"IFB_DEVICE=\"ifb{_config.Interface}\"");
        sb.AppendLine();

        // Baseline data
        sb.AppendLine("# Baseline speeds by day of week (0=Mon, 6=Sun) and hour");
        sb.AppendLine("declare -A BASELINE");
        foreach (var (key, value) in baseline.OrderBy(b => b.Key))
        {
            sb.AppendLine($"BASELINE[{key}]=\"{value}\"");
        }
        sb.AppendLine();

        // Ping configuration
        sb.AppendLine($"ISP_PING_HOST=\"{_config.PingHost}\"");
        sb.AppendLine($"BASELINE_LATENCY={_config.BaselineLatency} # ms (unloaded optimal ping)");
        sb.AppendLine($"LATENCY_THRESHOLD={_config.LatencyThreshold} # ms (threshold for latency increase)");
        sb.AppendLine($"LATENCY_DECREASE={_config.LatencyDecrease} # Incremental decrease in rate when latency exceeds threshold");
        sb.AppendLine($"LATENCY_INCREASE={_config.LatencyIncrease} # Increase when latency is normal or decreases");
        sb.AppendLine($"ABSOLUTE_MAX_DOWNLOAD_SPEED=\"{_config.AbsoluteMaxDownloadSpeed}\" # Max achievable download speed in Mbps");
        sb.AppendLine("MAX_DOWNLOAD_SPEED=$ABSOLUTE_MAX_DOWNLOAD_SPEED");
        sb.AppendLine();

        // Read speedtest result
        sb.AppendLine("# Read the base download speed from the stored speedtest result file");
        sb.AppendLine("if [ ! -f \"$SPEEDTEST_RESULTS_FILE\" ]; then");
        sb.AppendLine("    echo \"Speedtest result file not found. Exiting...\"");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Get the download speed from the file (from speedtest)");
        sb.AppendLine("SPEEDTEST_SPEED=$(cat \"$SPEEDTEST_RESULTS_FILE\" | awk '{print $4}') # in Mbps");
        sb.AppendLine();

        // Baseline lookup
        sb.AppendLine(GetBaselineBlendingLogicForPing());
        sb.AppendLine();

        // Ping latency measurement
        sb.AppendLine("# Get the current ping latency to the ISP server");
        sb.AppendLine($"latency=$(ping -I {_config.Interface} -c 20 -i 0.25 -q \"$ISP_PING_HOST\" | tail -n 1 | awk -F '/' '{{print $5}}')");
        sb.AppendLine();
        sb.AppendLine("# Calculate the number of deviations from the baseline threshold");
        sb.AppendLine("deviation_count=$(echo \"($latency - $BASELINE_LATENCY) / $LATENCY_THRESHOLD\" | bc)");
        sb.AppendLine();

        // Rate adjustment logic
        sb.AppendLine(GetLatencyAdjustmentLogic());
        sb.AppendLine();

        // Apply the adjustment
        sb.AppendLine("# Apply the max adjustment limit");
        sb.AppendLine("max_adjusted_rate=$(echo \"$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.95\" | bc)");
        sb.AppendLine("if (( $(echo \"$new_rate > $max_adjusted_rate\" | bc) )); then");
        sb.AppendLine("    new_rate=$max_adjusted_rate");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Round to 1 decimal place");
        sb.AppendLine("new_rate=$(echo \"scale=1; $new_rate / 1\" | bc)");
        sb.AppendLine();
        sb.AppendLine($"# Cap the rate at {_config.MaxDownloadSpeed} Mbps if it exceeds that");
        sb.AppendLine($"if (( $(echo \"$new_rate > {_config.MaxDownloadSpeed}\" | bc) )); then");
        sb.AppendLine($"    new_rate={_config.MaxDownloadSpeed}");
        sb.AppendLine("fi");
        sb.AppendLine();

        // TC class update function
        sb.AppendLine(GetTcUpdateFunction());
        sb.AppendLine();

        // Apply adjustment
        sb.AppendLine("# Adjust all TC classes with the new rate");
        sb.AppendLine("echo \"Adjusting rate classes to $new_rate Mbps...\"");
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $new_rate");
        sb.AppendLine();
        sb.AppendLine("# Show the updated tc class configuration");
        sb.AppendLine("echo \"Updated tc class configuration:\"");
        sb.AppendLine("tc class show dev $IFB_DEVICE | grep \"class htb\"");
        sb.AppendLine();
        sb.AppendLine("echo \"[$(date)] Adjusted rate to $new_rate Mbps\" >> /var/log/sqm-ping-adjust.log");

        return sb.ToString();
    }

    /// <summary>
    /// Generate install script
    /// </summary>
    public string GenerateInstallScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine("# SQM Installation Script for UniFi Cloud Gateway / Dream Machine");
        sb.AppendLine("# This script installs and configures adaptive SQM with baseline learning");
        sb.AppendLine();
        sb.AppendLine("echo \"Installing SQM scripts...\"");
        sb.AppendLine();
        sb.AppendLine("# Create directories");
        sb.AppendLine("mkdir -p /data/on-boot.d");
        sb.AppendLine("mkdir -p /data/scripts");
        sb.AppendLine();
        sb.AppendLine("# Copy boot scripts");
        sb.AppendLine("cp 20-sqm-speedtest-setup.sh /data/on-boot.d/");
        sb.AppendLine("cp 21-sqm-ping-setup.sh /data/on-boot.d/");
        sb.AppendLine("chmod +x /data/on-boot.d/20-sqm-speedtest-setup.sh");
        sb.AppendLine("chmod +x /data/on-boot.d/21-sqm-ping-setup.sh");
        sb.AppendLine();
        sb.AppendLine("# Copy adjustment scripts");
        sb.AppendLine("cp sqm-speedtest-adjust.sh /data/scripts/");
        sb.AppendLine("cp sqm-ping-adjust.sh /data/scripts/");
        sb.AppendLine("chmod +x /data/scripts/sqm-speedtest-adjust.sh");
        sb.AppendLine("chmod +x /data/scripts/sqm-ping-adjust.sh");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(_config.InfluxDbEndpoint))
        {
            sb.AppendLine("# Copy metrics collector script");
            sb.AppendLine("cp sqm-metrics-collector.sh /data/scripts/");
            sb.AppendLine("chmod +x /data/scripts/sqm-metrics-collector.sh");
            sb.AppendLine();
        }

        sb.AppendLine("echo \"Running initial setup...\"");
        sb.AppendLine();
        sb.AppendLine("# Run boot scripts to set up environment");
        sb.AppendLine("/data/on-boot.d/20-sqm-speedtest-setup.sh");
        sb.AppendLine("/data/on-boot.d/21-sqm-ping-setup.sh");
        sb.AppendLine();
        sb.AppendLine("echo \"SQM installation complete!\"");
        sb.AppendLine("echo \"\"");
        sb.AppendLine("echo \"The system will:\"");
        sb.AppendLine($"echo \"  - Run speedtest at: {string.Join(", ", _config.SpeedtestSchedule)}\"");
        sb.AppendLine($"echo \"  - Adjust rate every {_config.PingAdjustmentInterval} minutes based on latency\"");
        sb.AppendLine($"echo \"  - Monitor latency to {_config.PingHost}\"");
        sb.AppendLine("echo \"\"");
        sb.AppendLine("echo \"Logs:\"");
        sb.AppendLine("echo \"  - Speedtest: /var/log/sqm-speedtest-adjust.log\"");
        sb.AppendLine("echo \"  - Ping adjustments: /var/log/sqm-ping-adjust.log\"");

        return sb.ToString();
    }

    /// <summary>
    /// Generate metrics collector script for InfluxDB
    /// </summary>
    public string GenerateMetricsCollectorScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine();
        sb.AppendLine("# SQM Metrics Collector - sends data to InfluxDB");
        sb.AppendLine();
        sb.AppendLine($"INFLUXDB_URL=\"{_config.InfluxDbEndpoint}\"");
        sb.AppendLine($"INFLUXDB_TOKEN=\"{_config.InfluxDbToken}\"");
        sb.AppendLine($"INFLUXDB_ORG=\"{_config.InfluxDbOrg}\"");
        sb.AppendLine($"INFLUXDB_BUCKET=\"{_config.InfluxDbBucket}\"");
        sb.AppendLine($"INTERFACE=\"{_config.Interface}\"");
        sb.AppendLine($"IFB_DEVICE=\"ifb{_config.Interface}\"");
        sb.AppendLine();
        sb.AppendLine("# Get current rate from TC");
        sb.AppendLine("current_rate=$(tc class show dev $IFB_DEVICE | grep \"class htb 1:1\" | grep -o \"rate [0-9.]*[MGK]bit\" | awk '{print $2}' | sed 's/Mbit//')");
        sb.AppendLine();
        sb.AppendLine("# Get current latency");
        sb.AppendLine($"latency=$(ping -I $INTERFACE -c 5 -i 0.2 -q \"{_config.PingHost}\" | tail -n 1 | awk -F '/' '{{print $5}}')");
        sb.AppendLine();
        sb.AppendLine("# Read last speedtest result");
        sb.AppendLine("if [ -f /data/sqm-speedtest-result.txt ]; then");
        sb.AppendLine("    speedtest_speed=$(cat /data/sqm-speedtest-result.txt | awk '{print $4}')");
        sb.AppendLine("else");
        sb.AppendLine("    speedtest_speed=0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Send to InfluxDB");
        sb.AppendLine("curl -XPOST \"$INFLUXDB_URL/api/v2/write?org=$INFLUXDB_ORG&bucket=$INFLUXDB_BUCKET\" \\");
        sb.AppendLine("  -H \"Authorization: Token $INFLUXDB_TOKEN\" \\");
        sb.AppendLine("  -H \"Content-Type: text/plain; charset=utf-8\" \\");
        sb.AppendLine("  --data-binary \"sqm,interface=$INTERFACE current_rate=$current_rate,latency=$latency,speedtest_speed=$speedtest_speed\"");

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
        # Extract the classid (e.g., ""1:2"", ""1:4"", etc.)
        classid=$(echo ""$line"" | grep -o ""class htb [0-9:]*"" | awk '{print $3}')
        # Extract the prio value if present
        prio=$(echo ""$line"" | grep -o ""prio [0-9]*"" | awk '{print $2}')
        # Extract the rate value to check if it's a special class
        rate=$(echo ""$line"" | grep -o ""rate [0-9]*[a-zA-Z]*"" | awk '{print $2}')

        # Skip classes with rate > 64bit (these are special UniFi-configured classes)
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
        // Use connection-specific blending ratios
        var withinBaseline = _config.BlendingWeightWithin;
        var withinMeasured = 1.0 - withinBaseline;
        var belowBaseline = _config.BlendingWeightBelow;
        var belowMeasured = 1.0 - belowBaseline;

        var withinRatio = $"{(int)(withinBaseline * 100)}/{(int)(withinMeasured * 100)}";
        var belowRatio = $"{(int)(belowBaseline * 100)}/{(int)(belowMeasured * 100)}";

        return $@"# Get current day of week (0=Mon, 6=Sun) and hour
current_day=$(date +%u)  # 1=Mon, 7=Sun
current_day=$((current_day - 1))  # Convert to 0=Mon, 6=Sun
current_hour=$(date +%H | sed 's/^0//')  # Remove leading zero
lookup_key=""${{current_day}}_${{current_hour}}""

# Look up baseline speed for this day/hour
baseline_speed=${{BASELINE[$lookup_key]}}

if [ -n ""$baseline_speed"" ]; then
    echo ""Baseline speed for day $current_day hour $current_hour: $baseline_speed Mbps""

    # Calculate threshold (10% below baseline)
    threshold=$(echo ""scale=0; $baseline_speed * 0.9 / 1"" | bc)

    if [ ""$download_speed_mbps"" -ge ""$threshold"" ]; then
        # Within 10% of baseline: blend ({withinRatio} baseline/measured)
        blended_speed=$(echo ""scale=0; ($baseline_speed * {withinBaseline} + $download_speed_mbps * {withinMeasured}) / 1"" | bc)
        echo ""Speedtest within 10% of baseline: blending ({withinRatio} baseline/measured) → $blended_speed Mbps""
    else
        # More than 10% below baseline: favor baseline ({belowRatio} baseline/measured)
        blended_speed=$(echo ""scale=0; ($baseline_speed * {belowBaseline} + $download_speed_mbps * {belowMeasured}) / 1"" | bc)
        echo ""Speedtest below 10% threshold: weighting toward baseline ({belowRatio}) → $blended_speed Mbps""
    fi

    # Apply overhead
    download_speed_mbps=$(echo ""scale=0; $blended_speed * $DOWNLOAD_SPEED_MULTIPLIER / 1"" | bc)
    echo ""Speed after overhead: $download_speed_mbps Mbps""
else
    echo ""No baseline data for day $current_day hour $current_hour, using measured speed only""

    # Apply overhead to measured speed
    download_speed_mbps=$(echo ""scale=0; $download_speed_mbps * $DOWNLOAD_SPEED_MULTIPLIER / 1"" | bc)
    echo ""Speed after overhead: $download_speed_mbps Mbps""
fi";
    }

    /// <summary>
    /// Get baseline blending logic for ping script
    /// </summary>
    private string GetBaselineBlendingLogicForPing()
    {
        // Use connection-specific blending ratios (use the "within threshold" ratio for ping)
        var baselineWeight = _config.BlendingWeightWithin;
        var measuredWeight = 1.0 - baselineWeight;
        var ratio = $"{(int)(baselineWeight * 100)}/{(int)(measuredWeight * 100)}";

        return $@"# Get current day of week (0=Mon, 6=Sun) and hour
current_day=$(date +%u)  # 1=Mon, 7=Sun
current_day=$((current_day - 1))  # Convert to 0=Mon, 6=Sun
current_hour=$(date +%H | sed 's/^0//')  # Remove leading zero
lookup_key=""${{current_day}}_${{current_hour}}""

echo ""[$(date)] Baseline lookup: day=$current_day hour=$current_hour key=$lookup_key"" >> /var/log/sqm-ping-adjust.log

# Look up baseline speed for this day/hour
baseline_speed=${{BASELINE[$lookup_key]}}

# Use average of speedtest result (already has overhead) and baseline+overhead
if [ -n ""$baseline_speed"" ]; then
    # Apply overhead to baseline to match speedtest calculation
    baseline_with_overhead=$(echo ""scale=0; $baseline_speed * {_config.OverheadMultiplier} / 1"" | bc)
    # Cap baseline at MAX_DOWNLOAD_SPEED
    if [ ""$baseline_with_overhead"" -gt ""{_config.MaxDownloadSpeed}"" ]; then
        baseline_with_overhead={_config.MaxDownloadSpeed}
    fi
    # Blend speedtest result and baseline+overhead ({ratio} toward baseline)
    MAX_DOWNLOAD_SPEED=$(echo ""scale=0; ($baseline_with_overhead * {baselineWeight} + $SPEEDTEST_SPEED * {measuredWeight}) / 1"" | bc)
    echo ""[$(date)] Speedtest: $SPEEDTEST_SPEED Mbps, Baseline: $baseline_speed Mbps (+overhead = $baseline_with_overhead Mbps), Blended ({ratio}): $MAX_DOWNLOAD_SPEED Mbps"" >> /var/log/sqm-ping-adjust.log
    echo ""Speedtest (pre-padded): $SPEEDTEST_SPEED Mbps, Baseline: $baseline_speed Mbps (+overhead = $baseline_with_overhead Mbps), Blended ({ratio}): $MAX_DOWNLOAD_SPEED Mbps""
else
    # Speedtest result already has overhead applied
    MAX_DOWNLOAD_SPEED=$SPEEDTEST_SPEED
    echo ""[$(date)] No baseline for day $current_day hour $current_hour, using speedtest result: $MAX_DOWNLOAD_SPEED Mbps"" >> /var/log/sqm-ping-adjust.log
    echo ""No baseline for day $current_day hour $current_hour, using speedtest result: $MAX_DOWNLOAD_SPEED Mbps""
fi";
    }

    /// <summary>
    /// Get latency adjustment logic for ping script
    /// </summary>
    private string GetLatencyAdjustmentLogic()
    {
        return @"# If latency exceeds the baseline + threshold, reduce the rate
if (( $(echo ""$latency >= $BASELINE_LATENCY + $LATENCY_THRESHOLD"" | bc -l) )); then
    high_latency_message=""High latency detected: $latency ms (threshold: $BASELINE_LATENCY + $LATENCY_THRESHOLD ms)""
    echo $high_latency_message
    echo ""[$(date)] $high_latency_message"" >> /var/log/sqm-ping-adjust.log
    decrease_multiplier=$(echo ""$LATENCY_DECREASE^$deviation_count"" | bc -l)
    new_rate=$(echo ""$MAX_DOWNLOAD_SPEED * $decrease_multiplier"" | bc)

    # Enforce minimum rate
    if (( $(echo ""$new_rate < 180"" | bc) )); then
        new_rate=180
    fi

elif (( $(echo ""$latency < $BASELINE_LATENCY - 0.4"" | bc -l) )); then
    echo ""Latency is reduced: $latency ms""
    echo ""[$(date)] Latency is reduced: $latency ms"" >> /var/log/sqm-ping-adjust.log
    lower_bound=$(echo ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.92"" | bc)
    mid_bound=$(echo ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.94"" | bc)
    if (( $(echo ""$MAX_DOWNLOAD_SPEED < $lower_bound"" | bc -l) )); then
        echo ""Current speed test baseline is low, applying SQM bandwidth increase x2""
        echo ""[$(date)] Decision: MAX_DOWNLOAD_SPEED ($MAX_DOWNLOAD_SPEED) < lower_bound ($lower_bound), applying x2 increase"" >> /var/log/sqm-ping-adjust.log
        new_rate=$(echo ""$MAX_DOWNLOAD_SPEED * $LATENCY_INCREASE * $LATENCY_INCREASE"" | bc -l)
    elif (( $(echo ""$MAX_DOWNLOAD_SPEED < $mid_bound"" | bc -l) )); then
        echo ""Current speed test baseline is somewhat low, normalizing to optimal bandwidth""
        echo ""[$(date)] Decision: MAX_DOWNLOAD_SPEED ($MAX_DOWNLOAD_SPEED) < mid_bound ($mid_bound), normalizing to $mid_bound"" >> /var/log/sqm-ping-adjust.log
	new_rate=$mid_bound
    else
        echo ""[$(date)] Decision: Keeping rate at MAX_DOWNLOAD_SPEED ($MAX_DOWNLOAD_SPEED)"" >> /var/log/sqm-ping-adjust.log
        new_rate=$MAX_DOWNLOAD_SPEED
    fi

else
    echo ""Latency is normal: $latency ms""
    echo ""[$(date)] Latency is normal: $latency ms"" >> /var/log/sqm-ping-adjust.log
    lower_bound=$(echo ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.9"" | bc)
    mid_bound=$(echo ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.92"" | bc)

    latency_diff=$(echo ""$latency - $BASELINE_LATENCY"" | bc -l)
    latency_normal=$(echo ""$latency_diff <= 0.3"" | bc -l)

    if (( $(echo ""$MAX_DOWNLOAD_SPEED < $lower_bound"" | bc -l) )) &&
       (( latency_normal == 1 )); then
        echo ""Current speed test baseline is low and latency is within 0.3 ms of normal, applying SQM bandwidth increase""
        echo ""[$(date)] Decision: MAX_DOWNLOAD_SPEED ($MAX_DOWNLOAD_SPEED) < lower_bound ($lower_bound) and latency_diff ($latency_diff) <= 0.3, applying increase"" >> /var/log/sqm-ping-adjust.log
        new_rate=$(echo ""$MAX_DOWNLOAD_SPEED * $LATENCY_INCREASE"" | bc)
    elif (( $(echo ""$MAX_DOWNLOAD_SPEED < $mid_bound"" | bc -l) )) &&
       (( latency_normal == 1 )); then
        echo ""Current speed test baseline is somewhat low and latency is within 0.3 ms of normal, normalizing to optimal bandwidth""
        echo ""[$(date)] Decision: MAX_DOWNLOAD_SPEED ($MAX_DOWNLOAD_SPEED) < mid_bound ($mid_bound) and latency_diff ($latency_diff) <= 0.3, normalizing to $mid_bound"" >> /var/log/sqm-ping-adjust.log
	new_rate=$mid_bound
    else
        echo ""[$(date)] Decision: Keeping rate at MAX_DOWNLOAD_SPEED ($MAX_DOWNLOAD_SPEED), latency_diff=$latency_diff"" >> /var/log/sqm-ping-adjust.log
        new_rate=$MAX_DOWNLOAD_SPEED
    fi
fi";
    }
}
