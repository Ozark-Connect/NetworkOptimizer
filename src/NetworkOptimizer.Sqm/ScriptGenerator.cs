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
    public const string ManagedSpeedtestPath = "/data/network-optimizer/bin/speedtest";
    public const string ManagedSpeedtestArchiveRelease = "1.2.0";
    public const string ManagedSpeedtestCliVersion = "1.2.0.84";
    public const string ManagedSpeedtestBuildId = "ea6b6773cf";
    // Reviewed Ookla install.speedtest.net archives and Packagecloud Debian bookworm packages.
    // Both distributions contain the same executable for each architecture.
    public const string ManagedSpeedtestAarch64ArchiveSha256 = "3953d231da3783e2bf8904b6dd72767c5c6e533e163d3742fd0437affa431bd3";
    public const string ManagedSpeedtestAarch64DebSha256 = "98e7de9db3bf181d08bc67e647bcfc71349c8014e387289c08e54e5c55d82f37";
    public const string ManagedSpeedtestAarch64BinarySha256 = "d99fa13293f658b53eaa79fe81f4b210db39fdfc1e9698f33da3f234a6008df7";
    public const string ManagedSpeedtestArmhfArchiveSha256 = "e45fcdebbd8a185553535533dd032d6b10bc8c64eee4139b1147b9c09835d08d";
    public const string ManagedSpeedtestArmhfDebSha256 = "f00c46b4945e3e1fea08e4858db78c9d8f3e35a8c9daa4c033df7eb03931294d";
    public const string ManagedSpeedtestArmhfBinarySha256 = "66ad57568664e6f8580e14ad67316a57038fd22b30548bef98531df4ebcc8956";
    public const string ManagedSpeedtestX86_64ArchiveSha256 = "5690596c54ff9bed63fa3732f818a05dbc2db19ad36ed68f21ca5f64d5cfeeb7";
    public const string ManagedSpeedtestX86_64DebSha256 = "35e084567a6388631fb10cf01e5e0d6b57a67d34ede2b72ba111b3d9164c8b94";
    public const string ManagedSpeedtestX86_64BinarySha256 = "31f1124c5ab8acdae6b9fe1741e704df420f9f2e7d429679fabe62075453c051";

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
    /// <param name="baseline">Download schedule keyed "day_hour".</param>
    /// <param name="uploadBaseline">Upload schedule keyed "day_hour"; ignored unless the configuration shapes upload dynamically.</param>
    public Dictionary<string, string> GenerateAllScripts(Dictionary<string, string> baseline, Dictionary<string, string>? uploadBaseline = null)
    {
        return new Dictionary<string, string>
        {
            [$"20-sqm-{_name}.sh"] = GenerateBootScript(baseline, uploadBaseline)
        };
    }

    /// <summary>Whether the generated scripts carry an upload schedule at all.</summary>
    private bool UsesDynamicUpload(Dictionary<string, string>? uploadBaseline) =>
        _config.DynamicUpload && uploadBaseline is { Count: > 0 };

    /// <summary>
    /// Get the boot script filename for this configuration
    /// </summary>
    public string GetBootScriptName() => $"20-sqm-{_name}.sh";

    /// <summary>
    /// Generate the self-contained boot script that:
    /// 1. Installs dependencies (speedtest, jq)
    /// 2. Creates /data/sqm/ directory
    /// 3. Writes speedtest and ping scripts via heredoc
    /// 4. Sets up IFB device and TC classes
    /// 5. Configures crontab entries
    /// </summary>
    public string GenerateBootScript(Dictionary<string, string> baseline, Dictionary<string, string>? uploadBaseline = null)
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
        sb.AppendLine($"SPEEDTEST_BIN=\"{ManagedSpeedtestPath}\"");
        sb.AppendLine($"SPEEDTEST_RELEASE=\"{ManagedSpeedtestArchiveRelease}\"");
        sb.AppendLine($"SPEEDTEST_CLI_VERSION=\"{ManagedSpeedtestCliVersion}\"");
        sb.AppendLine($"SPEEDTEST_BUILD_ID=\"{ManagedSpeedtestBuildId}\"");
        sb.AppendLine();
        sb.AppendLine("log_speedtest_install_error() {");
        sb.AppendLine("    echo \"[$(date)] ERROR: $*\" >> \"$LOG_FILE\"");
        sb.AppendLine("    echo \"ERROR: $*\" >&2");
        sb.AppendLine("}");
        sb.AppendLine();
        // Rotate log on boot/deploy: keep last 2000 lines (~1.5 days at 1 min ping interval)
        sb.AppendLine("# Rotate log to prevent unbounded growth");
        sb.AppendLine("if [ -f \"$LOG_FILE\" ] && [ $(wc -l < \"$LOG_FILE\") -gt 2000 ]; then");
        sb.AppendLine("    tail -n 2000 \"$LOG_FILE\" > \"${LOG_FILE}.tmp\" && mv \"${LOG_FILE}.tmp\" \"$LOG_FILE\"");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("echo \"[$(date)] SQM boot script starting for $SQM_NAME ($INTERFACE)...\" >> $LOG_FILE");
        sb.AppendLine();

        // Section 1: Install dependencies
        sb.AppendLine("# ============================================");
        sb.AppendLine("# Section 1: Install Dependencies");
        sb.AppendLine("# ============================================");
        sb.AppendLine();
        sb.AppendLine("case \"$(uname -m)\" in");
        sb.AppendLine("    aarch64|arm64)");
        sb.AppendLine("        SPEEDTEST_ARCH=\"aarch64\"");
        sb.AppendLine($"        SPEEDTEST_ARCHIVE_SHA256=\"{ManagedSpeedtestAarch64ArchiveSha256}\"");
        sb.AppendLine("        SPEEDTEST_DEB_ARCH=\"arm64\"");
        sb.AppendLine($"        SPEEDTEST_DEB_SHA256=\"{ManagedSpeedtestAarch64DebSha256}\"");
        sb.AppendLine($"        SPEEDTEST_BINARY_SHA256=\"{ManagedSpeedtestAarch64BinarySha256}\"");
        sb.AppendLine("        ;;");
        sb.AppendLine("    armv7l|armv7)");
        sb.AppendLine("        SPEEDTEST_ARCH=\"armhf\"");
        sb.AppendLine($"        SPEEDTEST_ARCHIVE_SHA256=\"{ManagedSpeedtestArmhfArchiveSha256}\"");
        sb.AppendLine("        SPEEDTEST_DEB_ARCH=\"armhf\"");
        sb.AppendLine($"        SPEEDTEST_DEB_SHA256=\"{ManagedSpeedtestArmhfDebSha256}\"");
        sb.AppendLine($"        SPEEDTEST_BINARY_SHA256=\"{ManagedSpeedtestArmhfBinarySha256}\"");
        sb.AppendLine("        ;;");
        sb.AppendLine("    x86_64|amd64)");
        sb.AppendLine("        SPEEDTEST_ARCH=\"x86_64\"");
        sb.AppendLine($"        SPEEDTEST_ARCHIVE_SHA256=\"{ManagedSpeedtestX86_64ArchiveSha256}\"");
        sb.AppendLine("        SPEEDTEST_DEB_ARCH=\"amd64\"");
        sb.AppendLine($"        SPEEDTEST_DEB_SHA256=\"{ManagedSpeedtestX86_64DebSha256}\"");
        sb.AppendLine($"        SPEEDTEST_BINARY_SHA256=\"{ManagedSpeedtestX86_64BinarySha256}\"");
        sb.AppendLine("        ;;");
        sb.AppendLine("    *)");
        sb.AppendLine("        SPEEDTEST_ARCH=\"unsupported\"");
        sb.AppendLine("        SPEEDTEST_ARCHIVE_SHA256=\"unsupported\"");
        sb.AppendLine("        SPEEDTEST_BINARY_SHA256=\"unsupported\"");
        sb.AppendLine("        ;;");
        sb.AppendLine("esac");
        sb.AppendLine("SPEEDTEST_URL=\"https://install.speedtest.net/app/cli/ookla-speedtest-${SPEEDTEST_RELEASE}-linux-${SPEEDTEST_ARCH}.tgz\"");
        // Ookla publishes the same executable separately on Packagecloud; no repository setup or package install.
        sb.AppendLine("SPEEDTEST_DEB_URL=\"https://packagecloud.io/ookla/speedtest-cli/packages/debian/bookworm/speedtest_${SPEEDTEST_CLI_VERSION}-1.${SPEEDTEST_BUILD_ID}_${SPEEDTEST_DEB_ARCH}.deb/download.deb\"");
        sb.AppendLine();
        sb.AppendLine("speedtest_is_valid() {");
        sb.AppendLine("    [ \"$SPEEDTEST_BINARY_SHA256\" != \"unsupported\" ] && command -v sha256sum >/dev/null 2>&1 \\");
        sb.AppendLine("        && [ -f \"$SPEEDTEST_BIN\" ] && [ ! -L \"$SPEEDTEST_BIN\" ] && [ -x \"$SPEEDTEST_BIN\" ] \\");
        sb.AppendLine("        && printf '%s  %s\\n' \"$SPEEDTEST_BINARY_SHA256\" \"$SPEEDTEST_BIN\" | sha256sum -c - >/dev/null 2>&1 \\");
        sb.AppendLine("        && \"$SPEEDTEST_BIN\" --version 2>/dev/null | head -n 1 | grep -Fq \"Speedtest by Ookla ${SPEEDTEST_CLI_VERSION} (${SPEEDTEST_BUILD_ID})\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("install_managed_speedtest() (");
        sb.AppendLine("    for required_command in curl tar sha256sum; do");
        sb.AppendLine("        if ! command -v \"$required_command\" >/dev/null 2>&1; then");
        sb.AppendLine("            log_speedtest_install_error \"required command not found: $required_command\"");
        sb.AppendLine("            return 1");
        sb.AppendLine("        fi");
        sb.AppendLine("    done");
        sb.AppendLine("    if [ \"$SPEEDTEST_ARCH\" = \"unsupported\" ]; then");
        sb.AppendLine("        log_speedtest_install_error \"unsupported gateway architecture: $(uname -m)\"");
        sb.AppendLine("        return 1");
        sb.AppendLine("    fi");
        sb.AppendLine("    SPEEDTEST_DIR=$(dirname \"$SPEEDTEST_BIN\")");
        sb.AppendLine("    if ! mkdir -p \"$SPEEDTEST_DIR\"; then");
        sb.AppendLine("        log_speedtest_install_error \"could not create speedtest directory\"");
        sb.AppendLine("        return 1");
        sb.AppendLine("    fi");
        sb.AppendLine("    SPEEDTEST_STAGE=$(mktemp -d \"${SPEEDTEST_DIR}/.speedtest-install.XXXXXX\") || {");
        sb.AppendLine("        log_speedtest_install_error \"could not create speedtest staging directory\"");
        sb.AppendLine("        return 1");
        sb.AppendLine("    }");
        sb.AppendLine("    cleanup_speedtest_stage() {");
        sb.AppendLine("        rm -rf \"$SPEEDTEST_STAGE\"");
        sb.AppendLine("        rm -f \"${SPEEDTEST_BIN}.new\"");
        sb.AppendLine("    }");
        sb.AppendLine("    trap cleanup_speedtest_stage EXIT");
        sb.AppendLine("    trap 'exit 1' HUP INT TERM");
        sb.AppendLine("    if SPEEDTEST_HTTP_STATUS=$(curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --max-time 120 --max-filesize 4194304 --write-out '%{http_code}' \"$SPEEDTEST_URL\" -o \"$SPEEDTEST_STAGE/speedtest.tgz\"); then");
        sb.AppendLine("        printf '%s  %s\\n' \"$SPEEDTEST_ARCHIVE_SHA256\" \"$SPEEDTEST_STAGE/speedtest.tgz\" | sha256sum -c - >/dev/null 2>&1 \\");
        sb.AppendLine("            || { log_speedtest_install_error \"Ookla speedtest archive checksum mismatch\"; return 1; }");
        sb.AppendLine("        tar -xzf \"$SPEEDTEST_STAGE/speedtest.tgz\" -C \"$SPEEDTEST_STAGE\" speedtest \\");
        sb.AppendLine("            || { log_speedtest_install_error \"failed to extract Ookla speedtest\"; return 1; }");
        sb.AppendLine("    else");
        sb.AppendLine("        case \"$SPEEDTEST_HTTP_STATUS\" in");
        sb.AppendLine("            404|410) ;;");
        sb.AppendLine("            *) log_speedtest_install_error \"failed to download Ookla speedtest (HTTP $SPEEDTEST_HTTP_STATUS)\"; return 1 ;;");
        sb.AppendLine("        esac");
        sb.AppendLine("        echo \"[$(date)] Ookla archive unavailable (HTTP $SPEEDTEST_HTTP_STATUS); trying the verified Packagecloud package\" >> \"$LOG_FILE\"");
        sb.AppendLine("        command -v dpkg-deb >/dev/null 2>&1 \\");
        sb.AppendLine("            || { log_speedtest_install_error \"dpkg-deb is required for the Ookla package fallback\"; return 1; }");
        sb.AppendLine("        curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --max-time 120 --max-filesize 4194304 \"$SPEEDTEST_DEB_URL\" -o \"$SPEEDTEST_STAGE/speedtest.deb\" \\");
        sb.AppendLine("            || { log_speedtest_install_error \"failed to download Ookla speedtest fallback\"; return 1; }");
        sb.AppendLine("        printf '%s  %s\\n' \"$SPEEDTEST_DEB_SHA256\" \"$SPEEDTEST_STAGE/speedtest.deb\" | sha256sum -c - >/dev/null 2>&1 \\");
        sb.AppendLine("            || { log_speedtest_install_error \"Ookla speedtest package checksum mismatch\"; return 1; }");
        sb.AppendLine("        dpkg-deb --extract \"$SPEEDTEST_STAGE/speedtest.deb\" \"$SPEEDTEST_STAGE/package\" \\");
        sb.AppendLine("            || { log_speedtest_install_error \"failed to extract Ookla speedtest package\"; return 1; }");
        sb.AppendLine("        [ -f \"$SPEEDTEST_STAGE/package/usr/bin/speedtest\" ] && [ ! -L \"$SPEEDTEST_STAGE/package/usr/bin/speedtest\" ] \\");
        sb.AppendLine("            || { log_speedtest_install_error \"Ookla package did not contain a regular binary\"; return 1; }");
        sb.AppendLine("        cp \"$SPEEDTEST_STAGE/package/usr/bin/speedtest\" \"$SPEEDTEST_STAGE/speedtest\" \\");
        sb.AppendLine("            || { log_speedtest_install_error \"could not stage Ookla speedtest from package\"; return 1; }");
        sb.AppendLine("    fi");
        sb.AppendLine("    [ -f \"$SPEEDTEST_STAGE/speedtest\" ] && [ ! -L \"$SPEEDTEST_STAGE/speedtest\" ] \\");
        sb.AppendLine("        || { log_speedtest_install_error \"Ookla speedtest archive did not contain a regular binary\"; return 1; }");
        sb.AppendLine("    chmod 0755 \"$SPEEDTEST_STAGE/speedtest\" \\");
        sb.AppendLine("        || { log_speedtest_install_error \"could not make Ookla speedtest executable\"; return 1; }");
        sb.AppendLine("    printf '%s  %s\\n' \"$SPEEDTEST_BINARY_SHA256\" \"$SPEEDTEST_STAGE/speedtest\" | sha256sum -c - >/dev/null 2>&1 \\");
        sb.AppendLine("        || { log_speedtest_install_error \"Ookla speedtest binary checksum mismatch\"; return 1; }");
        sb.AppendLine("    mv -f \"$SPEEDTEST_STAGE/speedtest\" \"${SPEEDTEST_BIN}.new\" \\");
        sb.AppendLine("        || { log_speedtest_install_error \"could not stage Ookla speedtest\"; return 1; }");
        sb.AppendLine("    mv -f \"${SPEEDTEST_BIN}.new\" \"$SPEEDTEST_BIN\" \\");
        sb.AppendLine("        || { log_speedtest_install_error \"could not activate Ookla speedtest\"; return 1; }");
        sb.AppendLine("    speedtest_is_valid \\");
        sb.AppendLine("        || { log_speedtest_install_error \"installed Ookla speedtest failed validation\"; return 1; }");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("# A missing calibration CLI should not prevent the SQM scripts and cron entries from deploying.");
        sb.AppendLine("if ! speedtest_is_valid; then");
        sb.AppendLine("    echo \"[$(date)] Installing Ookla speedtest ${SPEEDTEST_CLI_VERSION}...\" >> \"$LOG_FILE\"");
        sb.AppendLine("    if ! install_managed_speedtest; then");
        sb.AppendLine("        echo \"[$(date)] WARNING: Ookla speedtest installation failed; continuing without adaptive calibration\" >> \"$LOG_FILE\"");
        sb.AppendLine("        echo \"WARNING: Ookla speedtest installation failed; continuing without adaptive calibration\" >&2");
        sb.AppendLine("    fi");
        sb.AppendLine("fi");
        sb.AppendLine();
        // Refresh the package index once if jq is missing, so a console with
        // stale/empty apt lists can still resolve it.
        sb.AppendLine("# Refresh package lists once if a base dependency is missing");
        sb.AppendLine("if ! which jq > /dev/null 2>&1; then");
        sb.AppendLine("    apt-get update");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Install jq if not present");
        sb.AppendLine("if ! which jq > /dev/null 2>&1; then");
        sb.AppendLine("    echo \"Installing jq...\" >> $LOG_FILE");
        sb.AppendLine("    apt-get install -y jq");
        sb.AppendLine("fi");
        sb.AppendLine();
        // An install that fails silently used to let the calibration run anyway.
        sb.AppendLine("# Verify what the generated scripts actually need before scheduling them");
        sb.AppendLine("for dep in awk jq; do");
        sb.AppendLine("    which \"$dep\" > /dev/null 2>&1 && continue");
        sb.AppendLine("    echo \"[$(date)] ERROR: dependency '$dep' is missing and could not be installed; Adaptive SQM will not change rates until it is present\" >> $LOG_FILE");
        sb.AppendLine("done");
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
        sb.Append(GenerateSpeedtestScript(baseline, uploadBaseline));
        sb.AppendLine("SPEEDTEST_EOF");
        sb.AppendLine("chmod +x \"$SPEEDTEST_SCRIPT\"");
        sb.AppendLine();

        // Section 4: Write ping script via heredoc
        sb.AppendLine("# ============================================");
        sb.AppendLine("# Section 4: Create Ping Adjustment Script");
        sb.AppendLine("# ============================================");
        sb.AppendLine();
        sb.AppendLine("cat > \"$PING_SCRIPT\" << 'PING_EOF'");
        sb.Append(GeneratePingScript(baseline, uploadBaseline));
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
    private string GenerateSpeedtestScript(Dictionary<string, string> baseline, Dictionary<string, string>? uploadBaseline)
    {
        var dynamicUpload = UsesDynamicUpload(uploadBaseline);
        var uploadRateVar = dynamicUpload ? "$upload_rate" : "$UPLOAD_SPEED";

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
        sb.AppendLine($"SPEEDTEST_PROBE_RATE=\"{_config.SpeedtestProbeRateMbps}\"");
        sb.AppendLine($"MIN_DOWNLOAD_SPEED=\"{_config.MinDownloadSpeed}\"");
        sb.AppendLine($"UPLOAD_SPEED=\"{_config.NominalUploadSpeed}\"");
        sb.AppendLine($"SHAPE_UPLOAD={(_config.ShapeUpload ? "1" : "0")}");
        if (dynamicUpload)
            sb.AppendLine($"MIN_UPLOAD_SPEED=\"{_config.MinUploadSpeed}\"");
        sb.AppendLine($"DOWNLOAD_BURST_MODE={(_config.RateProportionalDownloadBurst ? "1" : "0")}");
        sb.AppendLine($"DOWNLOAD_SPEED_MULTIPLIER=\"{Inv(_config.OverheadMultiplier)}\"");
        if (_config.MeasuredToShapedFactor != 1.0)
            sb.AppendLine($"MEASURED_TO_SHAPED=\"{Inv(_config.MeasuredToShapedFactor)}\"");
        sb.AppendLine($"SAFETY_CAP=\"{Inv(_config.SafetyCapPercent)}\"");
        // Physical link speed final clamp (0 = unknown, skip clamp). LINK_SPEED_HEADROOM reserves
        // headroom below physical line rate so HTB can shape without buffering at the NIC.
        sb.AppendLine($"WAN_LINK_SPEED_MBPS=\"{_config.WanLinkSpeedMbps ?? 0}\"");
        sb.AppendLine($"LINK_SPEED_HEADROOM=\"0.98\"");
        sb.AppendLine($"RESULT_FILE=\"/data/sqm/{_name}-result.txt\"");
        sb.AppendLine($"LOG_FILE=\"/var/log/sqm-{_name}.log\"");
        sb.AppendLine($"SPEEDTEST_BIN=\"{ManagedSpeedtestPath}\"");
        sb.AppendLine($"SPEEDTEST_CLI_VERSION=\"{ManagedSpeedtestCliVersion}\"");
        sb.AppendLine($"SPEEDTEST_BUILD_ID=\"{ManagedSpeedtestBuildId}\"");
        sb.AppendLine("case \"$(uname -m)\" in");
        sb.AppendLine($"    aarch64|arm64) SPEEDTEST_BINARY_SHA256=\"{ManagedSpeedtestAarch64BinarySha256}\" ;;");
        sb.AppendLine($"    armv7l|armv7) SPEEDTEST_BINARY_SHA256=\"{ManagedSpeedtestArmhfBinarySha256}\" ;;");
        sb.AppendLine($"    x86_64|amd64) SPEEDTEST_BINARY_SHA256=\"{ManagedSpeedtestX86_64BinarySha256}\" ;;");
        sb.AppendLine("    *) SPEEDTEST_BINARY_SHA256=\"unsupported\" ;;");
        sb.AppendLine("esac");
        sb.AppendLine();

        // Baseline data
        sb.AppendLine("# Baseline speeds by day of week (0=Mon, 6=Sun) and hour");
        sb.AppendLine("declare -A BASELINE");
        foreach (var (key, value) in baseline.OrderBy(b => b.Key))
        {
            sb.AppendLine($"BASELINE[{key}]=\"{value}\"");
        }
        sb.AppendLine();
        if (dynamicUpload)
            AppendUploadBaseline(sb, uploadBaseline!);

        sb.AppendLine(GetArithmeticFunctions());
        sb.AppendLine();

        // Runs before the probe-rate lift below: bailing here leaves tc untouched, not opened up.
        sb.AppendLine("# awk runs every rate calculation, jq parses the speedtest JSON.");
        sb.AppendLine("# Every firmware upgrade drops apt-installed packages, so try once to restore them.");
        sb.AppendLine("for dep in awk jq; do");
        sb.AppendLine("    which \"$dep\" > /dev/null 2>&1 && continue");
        sb.AppendLine("    case \"$dep\" in awk) pkg=mawk ;; *) pkg=\"$dep\" ;; esac");
        sb.AppendLine("    echo \"[$(date)] $dep is missing, installing $pkg...\" >> $LOG_FILE");
        sb.AppendLine("    apt-get install -y \"$pkg\" >> $LOG_FILE 2>&1");
        sb.AppendLine("    # Retry behind a refreshed index: an upgrade can leave the apt lists stale or empty.");
        sb.AppendLine("    if ! which \"$dep\" > /dev/null 2>&1; then");
        sb.AppendLine("        apt-get update >> $LOG_FILE 2>&1");
        sb.AppendLine("        apt-get install -y \"$pkg\" >> $LOG_FILE 2>&1");
        sb.AppendLine("    fi");
        sb.AppendLine("    if ! which \"$dep\" > /dev/null 2>&1; then");
        sb.AppendLine("        echo \"[$(date)] ERROR: $dep still missing after install, leaving tc untouched\" >> $LOG_FILE");
        sb.AppendLine("        exit 1");
        sb.AppendLine("    fi");
        sb.AppendLine("done");
        sb.AppendLine();

        // Check for speedtest
        sb.AppendLine("# Validate the exact managed speedtest build before changing TC rates");
        sb.AppendLine("if [ \"$SPEEDTEST_BINARY_SHA256\" = \"unsupported\" ] \\");
        sb.AppendLine("    || [ ! -f \"$SPEEDTEST_BIN\" ] || [ -L \"$SPEEDTEST_BIN\" ] || [ ! -x \"$SPEEDTEST_BIN\" ] \\");
        sb.AppendLine("    || ! printf '%s  %s\\n' \"$SPEEDTEST_BINARY_SHA256\" \"$SPEEDTEST_BIN\" | sha256sum -c - >/dev/null 2>&1 \\");
        sb.AppendLine("    || ! \"$SPEEDTEST_BIN\" --version 2>/dev/null | head -n 1 | grep -Fq \"Speedtest by Ookla ${SPEEDTEST_CLI_VERSION} (${SPEEDTEST_BUILD_ID})\"; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: managed speedtest binary is missing or failed validation\" >> $LOG_FILE");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();

        sb.AppendLine("echo \"[$(date)] Starting speedtest adjustment on $INTERFACE...\" >> $LOG_FILE");
        sb.AppendLine();

        // A probe (congestion learning sample) may hold the shaper lifted for ~15 s; calibrating on
        // top of it would measure against its lift and then write over its restore.
        sb.AppendLine(GetProbeLockWait());
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

        // Set probe rate slightly above max shaping rate before speedtest so TC never engages
        sb.AppendLine("# Set SQM to probe rate (3% above max shaping rate) before speedtest for unshaped measurement");
        // The probe runs with the configured burst mode too: with the conservative bucket a
        // throughput-limited path under-measures itself here, and that low number is what the
        // adaptive rate is derived from.
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $SPEEDTEST_PROBE_RATE $DOWNLOAD_BURST_MODE");
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
        sb.AppendLine($"speedtest_output=$(\"$SPEEDTEST_BIN\" --accept-license --accept-gdpr --format=json --interface=$INTERFACE{serverIdArg})");
        sb.AppendLine();
        sb.AppendLine("# Parse download speed (bytes/sec to Mbps)");
        sb.AppendLine("download_speed_bytes=$(echo \"$speedtest_output\" | jq .download.bandwidth)");
        sb.AppendLine("download_speed_mbps=$(num_i \"$download_speed_bytes * 8 / 1000000\")");
        sb.AppendLine();
        sb.AppendLine("echo \"[$(date)] Measured: $download_speed_mbps Mbps\" >> $LOG_FILE");
        sb.AppendLine();
        if (_config.MeasuredToShapedFactor != 1.0)
        {
            sb.AppendLine("# Learned profile: the schedule is in shaper rates, so convert the payload figure first");
            sb.AppendLine("download_speed_mbps=$(num_i \"$download_speed_mbps * $MEASURED_TO_SHAPED\")");
            sb.AppendLine();
        }

        // Apply floor
        sb.AppendLine("# Apply minimum floor");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps < MIN_DOWNLOAD_SPEED ? MIN_DOWNLOAD_SPEED : download_speed_mbps))");
        sb.AppendLine();

        // Baseline blending
        sb.AppendLine(GetBaselineBlendingLogic());
        sb.AppendLine();
        if (dynamicUpload)
        {
            sb.AppendLine(GetUploadRateLogic());
            sb.AppendLine();
        }

        // Apply ceiling
        sb.AppendLine("# Apply ceiling");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps > MAX_DOWNLOAD_SPEED ? MAX_DOWNLOAD_SPEED : download_speed_mbps))");
        sb.AppendLine();

        // Apply safety cap
        sb.AppendLine("# Apply safety cap");
        sb.AppendLine("max_adjusted_rate=$(num_i \"$MAX_DOWNLOAD_SPEED * $SAFETY_CAP\")");
        sb.AppendLine("download_speed_mbps=$((download_speed_mbps > max_adjusted_rate ? max_adjusted_rate : download_speed_mbps))");
        sb.AppendLine();

        // Apply physical link speed ceiling (with HTB headroom) as final clamp
        sb.AppendLine("# Apply physical link speed ceiling (HTB headroom below line rate)");
        sb.AppendLine("if [ \"$WAN_LINK_SPEED_MBPS\" -gt 0 ]; then");
        sb.AppendLine("    link_ceiling=$(num_i \"$WAN_LINK_SPEED_MBPS * $LINK_SPEED_HEADROOM\")");
        sb.AppendLine("    if [ \"$download_speed_mbps\" -gt \"$link_ceiling\" ]; then");
        sb.AppendLine("        download_speed_mbps=$link_ceiling");
        sb.AppendLine("    fi");
        sb.AppendLine("fi");
        sb.AppendLine();

        // The probe-rate lift above is still in effect, so an unusable rate must restore rather
        // than exit and leave download unshaped.
        sb.AppendLine("# Refuse an unusable rate, and restore rather than leave the probe rate in place");
        sb.AppendLine("if ! rate_is_valid \"$download_speed_mbps\"; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: computed rate '$download_speed_mbps' is unusable, not applying\" >> $LOG_FILE");
        sb.AppendLine("    previous_rate=$(awk '{print $4}' \"$RESULT_FILE\" 2>/dev/null)");
        sb.AppendLine("    if rate_is_valid \"$previous_rate\"; then");
        sb.AppendLine("        echo \"[$(date)] Restoring last good rate $previous_rate Mbps\" >> $LOG_FILE");
        sb.AppendLine("        update_all_tc_classes $IFB_DEVICE $previous_rate $DOWNLOAD_BURST_MODE");
        sb.AppendLine("    else");
        sb.AppendLine("        echo \"[$(date)] ERROR: no last good rate to restore, download left at probe rate $SPEEDTEST_PROBE_RATE Mbps\" >> $LOG_FILE");
        sb.AppendLine("    fi");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Written only once the rate is known good: a bad run must not poison the ping script.
        sb.AppendLine("# Save result for ping script");
        sb.AppendLine("echo \"Measured download speed: $download_speed_mbps Mbps\" > \"$RESULT_FILE\"");
        sb.AppendLine();
        sb.AppendLine("# Apply TC classes (downstream and upstream)");
        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $download_speed_mbps $DOWNLOAD_BURST_MODE");
        sb.AppendLine("# Upstream: shape rate if enabled, otherwise just tune performance params");
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine($"    update_all_tc_classes $INTERFACE {uploadRateVar}");
        sb.AppendLine("else");
        sb.AppendLine("    tune_tc_performance $INTERFACE");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine($"    echo \"[$(date)] Adjusted to $download_speed_mbps Mbps (down), {uploadRateVar} Mbps (up)\" >> $LOG_FILE");
        sb.AppendLine("else");
        sb.AppendLine("    echo \"[$(date)] Adjusted to $download_speed_mbps Mbps (down), upstream perf-tuned\" >> $LOG_FILE");
        sb.AppendLine("fi");

        return sb.ToString();
    }

    /// <summary>
    /// Generate the ping adjustment script content (embedded in boot script)
    /// </summary>
    private string GeneratePingScript(Dictionary<string, string> baseline, Dictionary<string, string>? uploadBaseline)
    {
        var dynamicUpload = UsesDynamicUpload(uploadBaseline);
        var uploadRateVar = dynamicUpload ? "$upload_rate" : "$UPLOAD_SPEED";

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
        if (dynamicUpload)
            sb.AppendLine($"MIN_UPLOAD_SPEED=\"{_config.MinUploadSpeed}\"");
        sb.AppendLine($"DOWNLOAD_BURST_MODE={(_config.RateProportionalDownloadBurst ? "1" : "0")}");
        sb.AppendLine($"SAFETY_CAP=\"{Inv(_config.SafetyCapPercent)}\"");
        sb.AppendLine($"NOMINAL_SPEED=\"{_config.NominalDownloadSpeed}\"");
        // Physical link speed final clamp (0 = unknown, skip clamp). LINK_SPEED_HEADROOM reserves
        // headroom below physical line rate so HTB can shape without buffering at the NIC.
        sb.AppendLine($"WAN_LINK_SPEED_MBPS=\"{_config.WanLinkSpeedMbps ?? 0}\"");
        sb.AppendLine($"LINK_SPEED_HEADROOM=\"0.98\"");
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
        if (dynamicUpload)
            AppendUploadBaseline(sb, uploadBaseline!);

        // A probe (congestion learning sample) holds the shaper lifted for ~15 s and leaves this
        // lock while it does. Adjusting in that window would write a latency-driven cut over the
        // lift and shorten the measurement; the probe restores the rates itself when it exits.
        sb.AppendLine(GetArithmeticFunctions());
        sb.AppendLine();

        // Check only: this runs every minute and must not touch apt.
        sb.AppendLine("# awk runs every rate calculation here");
        sb.AppendLine("if ! which awk > /dev/null 2>&1; then");
        sb.AppendLine("    echo \"[$(date)] ERROR: awk not found, skipping ping adjustment\" >> $LOG_FILE");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();

        sb.AppendLine(GetProbeLockGuard());
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
        sb.AppendLine("if (( $(num_bool \"$SPEEDTEST_SPEED <= 0\") )) || (( $(num_bool \"$SPEEDTEST_SPEED > 100000\") )); then");
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
        if (dynamicUpload)
        {
            sb.AppendLine(GetUploadRateLogic());
            sb.AppendLine();
        }

        // Apply safety cap to MAX_DOWNLOAD_SPEED BEFORE latency adjustment.
        // This sets the schedule-derived ceiling as the starting point, then latency
        // can freely decrease below it. Without this, the cap creates a dead zone where
        // mild latency spikes are detected but produce no visible rate change.
        var useBaselineRatio = _config.ConnectionType is ConnectionType.Gpon or ConnectionType.XgsPon;
        if (useBaselineRatio)
        {
            sb.AppendLine("# Apply baseline-proportional safety cap before latency adjustment (fiber)");
            sb.AppendLine("if [ -n \"$baseline_speed\" ] && [ \"$NOMINAL_SPEED\" -gt 0 ]; then");
            sb.AppendLine("    baseline_ratio=$(num_s 4 \"$baseline_speed / $NOMINAL_SPEED\")");
            sb.AppendLine("    max_adjusted_rate=$(num_i \"$ABSOLUTE_MAX_DOWNLOAD_SPEED * $SAFETY_CAP * $baseline_ratio\")");
            sb.AppendLine("else");
            sb.AppendLine("    max_adjusted_rate=$(num_f \"$ABSOLUTE_MAX_DOWNLOAD_SPEED * $SAFETY_CAP\")");
            sb.AppendLine("fi");
        }
        else
        {
            sb.AppendLine("# Apply flat safety cap before latency adjustment");
            sb.AppendLine("max_adjusted_rate=$(num_f \"$ABSOLUTE_MAX_DOWNLOAD_SPEED * $SAFETY_CAP\")");
        }
        sb.AppendLine("if (( $(num_bool \"$MAX_DOWNLOAD_SPEED > $max_adjusted_rate\") )); then");
        sb.AppendLine("    MAX_DOWNLOAD_SPEED=$(num_i \"$max_adjusted_rate\")");
        sb.AppendLine("fi");
        sb.AppendLine();

        // Physical link speed ceiling (with HTB headroom) as final clamp on the schedule cap
        sb.AppendLine("# Apply physical link speed ceiling (HTB headroom below line rate)");
        sb.AppendLine("if [ \"$WAN_LINK_SPEED_MBPS\" -gt 0 ]; then");
        sb.AppendLine("    link_ceiling=$(num_i \"$WAN_LINK_SPEED_MBPS * $LINK_SPEED_HEADROOM\")");
        sb.AppendLine("    if (( $(num_bool \"$max_adjusted_rate > $link_ceiling\") )); then");
        sb.AppendLine("        max_adjusted_rate=$link_ceiling");
        sb.AppendLine("    fi");
        sb.AppendLine("    if (( $(num_bool \"$MAX_DOWNLOAD_SPEED > $link_ceiling\") )); then");
        sb.AppendLine("        MAX_DOWNLOAD_SPEED=$link_ceiling");
        sb.AppendLine("    fi");
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
        sb.AppendLine("deviation_count=$(num_i \"($latency - $BASELINE_LATENCY) / $LATENCY_THRESHOLD\")");
        sb.AppendLine();

        // Latency adjustment logic (operates on capped MAX_DOWNLOAD_SPEED, can decrease freely)
        sb.AppendLine(GetLatencyAdjustmentLogic());
        sb.AppendLine();

        // Post-latency ceiling: prevent increase branch from exceeding schedule cap
        sb.AppendLine("if (( $(num_bool \"$new_rate > $max_adjusted_rate\") )); then");
        sb.AppendLine("    new_rate=$max_adjusted_rate");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("new_rate=$(num_s 1 \"$new_rate\")");
        sb.AppendLine();
        sb.AppendLine("if (( $(num_bool \"$new_rate > $MAX_DOWNLOAD_SPEED_CONFIG\") )); then");
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

        if (dynamicUpload)
        {
            // The ping cannot tell which direction is saturated, so a latency cut scales upload
            // by the same fraction it took off download. The floor still holds.
            sb.AppendLine("# Latency cut applies to upload in the same proportion");
            sb.AppendLine("if (( $(num_bool \"$new_rate < $MAX_DOWNLOAD_SPEED\") )); then");
            sb.AppendLine("    upload_rate=$(num_i \"$upload_rate * $new_rate / $MAX_DOWNLOAD_SPEED\")");
            sb.AppendLine("    if [ \"$upload_rate\" -lt \"$MIN_UPLOAD_SPEED\" ]; then upload_rate=$MIN_UPLOAD_SPEED; fi");
            sb.AppendLine("fi");
            sb.AppendLine();
        }

        // TC update function and apply
        sb.AppendLine(GetTcUpdateFunction());
        sb.AppendLine();

        // Skip tc update if rate hasn't changed (avoids no-op tc rewrites every minute)
        sb.AppendLine("# Skip tc update if rate unchanged");
        sb.AppendLine("current_rate=$(tc class show dev $IFB_DEVICE 2>/dev/null | grep \"class htb 1:1 root\" | grep -o \"rate [0-9]*Mbit\" | grep -o \"[0-9]*\")");
        if (dynamicUpload)
        {
            sb.AppendLine("current_up_rate=$(tc class show dev $INTERFACE 2>/dev/null | grep \"class htb 1:1 root\" | grep -o \"rate [0-9]*Mbit\" | grep -o \"[0-9]*\")");
            sb.AppendLine("if [ \"$new_rate_int\" = \"$current_rate\" ] && [ \"$upload_rate\" = \"$current_up_rate\" ]; then");
        }
        else
        {
            sb.AppendLine("if [ \"$new_rate_int\" = \"$current_rate\" ]; then");
        }
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();

        sb.AppendLine("update_all_tc_classes $IFB_DEVICE $new_rate_int $DOWNLOAD_BURST_MODE");
        sb.AppendLine("# Upstream: shape rate if enabled, otherwise just tune performance params");
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine($"    update_all_tc_classes $INTERFACE {uploadRateVar}");
        sb.AppendLine("else");
        sb.AppendLine("    tune_tc_performance $INTERFACE");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("if [ \"$SHAPE_UPLOAD\" = \"1\" ]; then");
        sb.AppendLine($"    echo \"[$(date)] Ping adjusted to $new_rate_int Mbps (down), {uploadRateVar} Mbps (up) (latency: ${{latency}}ms)\" >> $LOG_FILE");
        sb.AppendLine("else");
        sb.AppendLine("    echo \"[$(date)] Ping adjusted to $new_rate_int Mbps (down), upstream perf-tuned (latency: ${latency}ms)\" >> $LOG_FILE");
        sb.AppendLine("fi");

        return sb.ToString();
    }

    /// <summary>Path of the lock a probe holds while it has the shaper lifted on an interface.</summary>
    public static string ProbeLockPath(string interfaceName) => $"/data/sqm/probe-{interfaceName}.lock";

    /// <summary>Seconds after which a lock left behind by a killed probe is ignored and removed.</summary>
    public const int ProbeLockMaxAgeSeconds = 120;

    /// <summary>
    /// Speedtest-script wait: let a fresh probe lock clear (up to 90 s) before calibrating, and
    /// drop a stale one so a killed probe can never block calibration for good.
    /// </summary>
    private static string GetProbeLockWait()
    {
        return $@"# Wait for a probe (congestion learning sample) to release the shaper
PROBE_LOCK=""/data/sqm/probe-${{INTERFACE}}.lock""
for _ in $(seq 1 90); do
    [ -f ""$PROBE_LOCK"" ] || break
    lock_age=$(( $(date +%s) - $(stat -c %Y ""$PROBE_LOCK"" 2>/dev/null || echo 0) ))
    if [ ""$lock_age"" -ge {ProbeLockMaxAgeSeconds} ]; then rm -f ""$PROBE_LOCK""; break; fi
    sleep 1
done";
    }

    /// <summary>
    /// Ping-script guard: stand down while a fresh probe lock exists for this interface.
    /// </summary>
    private static string GetProbeLockGuard()
    {
        return $@"# Stand down while a probe (congestion learning sample) has the shaper lifted
PROBE_LOCK=""/data/sqm/probe-${{INTERFACE}}.lock""
if [ -f ""$PROBE_LOCK"" ]; then
    lock_age=$(( $(date +%s) - $(stat -c %Y ""$PROBE_LOCK"" 2>/dev/null || echo 0) ))
    if [ ""$lock_age"" -lt {ProbeLockMaxAgeSeconds} ]; then
        exit 0
    fi
    rm -f ""$PROBE_LOCK""
fi";
    }

    /// <summary>
    /// Emit the upload schedule array (same "day_hour" keys as BASELINE).
    /// </summary>
    private static void AppendUploadBaseline(StringBuilder sb, Dictionary<string, string> uploadBaseline)
    {
        sb.AppendLine("# Upload schedule by day of week (0=Mon, 6=Sun) and hour");
        sb.AppendLine("declare -A UPLOAD_BASELINE");
        foreach (var (key, value) in uploadBaseline.OrderBy(b => b.Key))
        {
            sb.AppendLine($"UPLOAD_BASELINE[{key}]=\"{value}\"");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Upload rate for the current quarter hour. Runs after the download baseline lookup, which
    /// set lookup_key, next_key, and current_min; the same 15-minute interpolation applies.
    /// </summary>
    private static string GetUploadRateLogic()
    {
        return @"# Upload schedule (interpolated at the same quarter-hour breakpoints as the download baseline)
upload_rate=$UPLOAD_SPEED
upload_baseline=${UPLOAD_BASELINE[$lookup_key]}
next_upload_baseline=${UPLOAD_BASELINE[$next_key]}
if [ -n ""$upload_baseline"" ] && [ -n ""$next_upload_baseline"" ]; then
    upload_weight=$(( (current_min / 15) * 25 ))
    upload_rate=$(( (upload_baseline * (100 - upload_weight) + next_upload_baseline * upload_weight) / 100 ))
elif [ -n ""$upload_baseline"" ]; then
    upload_rate=$upload_baseline
fi
if [ ""$upload_rate"" -lt ""$MIN_UPLOAD_SPEED"" ]; then upload_rate=$MIN_UPLOAD_SPEED; fi
if [ ""$upload_rate"" -gt ""$UPLOAD_SPEED"" ]; then upload_rate=$UPLOAD_SPEED; fi";
    }

    /// <summary>
    /// Get TC update function (common to both scripts)
    /// </summary>
    private string GetTcUpdateFunction() => TcFunctionsText;

    /// <summary>The shell arithmetic helpers every generated rate calculation goes through.</summary>
    private static string GetArithmeticFunctions() => ArithmeticFunctionsText;

    /// <summary>
    /// Arithmetic helpers built on awk. Never reintroduce bc: it is not in the firmware base, so
    /// every upgrade drops it, and one reported UCG-Max never got it back - rates came out empty.
    /// LC_ALL=C is required because awk honours LC_NUMERIC for printf.
    /// num_i truncates like "scale=0; x / 1", num_s sets places like "scale=N", num_f keeps bc's
    /// operand scale, num_bool prints the 1 or 0 that (( )) expects.
    /// </summary>
    internal static string ArithmeticFunctionsText =>
        @"# Arithmetic helpers (awk, never bc - see ScriptGenerator.ArithmeticFunctionsText)
num_i() { LC_ALL=C awk ""BEGIN{print int($*)}""; }
num_f() { LC_ALL=C awk ""BEGIN{printf \""%.6f\"", $*}""; }
num_s() { local places=$1; shift; LC_ALL=C awk -v p=""$places"" ""BEGIN{printf \""%.*f\"", p, $*}""; }
num_bool() { LC_ALL=C awk ""BEGIN{print ($*)?1:0}""; }";

    /// <summary>
    /// The shell functions that size burst and fq_codel memory and rewrite the HTB classes. Shared
    /// with the shaper-lift wrapper so there is exactly one copy of the tc logic.
    /// </summary>
    internal static string TcFunctionsText =>
        @"# Burst sizing. Mode 0 (default) is the conservative sizing: 5KB burst eliminates
# downstream drop_overmemory for bulk flows at gig speeds, and 8KB+ creates bursty HTB
# send patterns that increase queue depth variance in fq_codel.
#
# Mode 1 is the rate-proportional sizing (~1 ms of line time), for paths where the
# conservative bucket is only 1-3 packets: at 900 Mbit, 5KB is ~44 us of line time, so HTB
# releases only a few packets per timer wakeup and throughput degenerates into a function
# of scheduling latency. It is a per-WAN setting, on for new WANs: it showed no gain on other
# platforms, raised the loaded latency tail on the path where it did help, and can compound
# with an upstream token-bucket policer. Download (IFB) path only; egress always uses mode 0.
calc_burst() {
    local rate_mbps=$1
    local mode=${2:-0}
    local burst
    if [ ""$mode"" = ""1"" ]; then
        burst=$((rate_mbps * 125))
        [ ""$burst"" -lt 1500 ] && burst=1500
        [ ""$burst"" -gt 131072 ] && burst=131072
    else
        burst=$((rate_mbps * 5))
        [ ""$burst"" -lt 1500 ] && burst=1500
        [ ""$burst"" -gt 5000 ] && burst=5000
    fi
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
# Usable means a number carrying a non-zero digit. Pure shell: the arithmetic that produced the
# value may be exactly what failed, and an integer test would truncate a fractional rate to 0.
rate_is_valid() {
    [ -n ""$1"" ] || return 1
    echo ""$1"" | grep -qE '^[0-9]+\.?[0-9]*$' || return 1
    echo ""$1"" | grep -q '[1-9]'
}

update_all_tc_classes() {
    local device=$1
    local new_rate=$2
    # rate 0Mbit on the root class takes the whole direction down.
    if ! rate_is_valid ""$new_rate""; then
        echo ""[$(date)] ERROR: refusing tc update on $device, rate '$new_rate' is not a usable rate"" >> ""${LOG_FILE:-/dev/null}""
        return 1
    fi
    # Burst mode is per-WAN and download-only: callers on the IFB pass $DOWNLOAD_BURST_MODE,
    # egress callers omit it and stay on the conservative sizing.
    local burst_mode=${3:-0}
    local burst=$(calc_burst $new_rate $burst_mode)
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

        return $@"# Baseline blending (with quarter-hour interpolation)
current_day=$(date +%u)
current_day=$((current_day - 1))
current_hour=$(date +%H | sed 's/^0//')
current_min=$(date +%M | sed 's/^0//')
lookup_key=""${{current_day}}_${{current_hour}}""

# Next hour lookup (wraps at midnight to next day)
next_hour=$(( (current_hour + 1) % 24 ))
if [ ""$next_hour"" -eq 0 ]; then
    next_day=$(( (current_day + 1) % 7 ))
else
    next_day=$current_day
fi
next_key=""${{next_day}}_${{next_hour}}""

baseline_speed=${{BASELINE[$lookup_key]}}
next_baseline_speed=${{BASELINE[$next_key]}}

# Interpolate at 15-minute breakpoints: :00=0%, :15=25%, :30=50%, :45=75%
if [ -n ""$baseline_speed"" ] && [ -n ""$next_baseline_speed"" ]; then
    quarter=$(( current_min / 15 ))
    weight=$(( quarter * 25 ))
    baseline_speed=$(( (baseline_speed * (100 - weight) + next_baseline_speed * weight) / 100 ))
    if [ ""$baseline_speed"" -lt 5 ]; then baseline_speed=5; fi
fi

if [ -n ""$baseline_speed"" ]; then
    threshold=$(num_i ""$baseline_speed * 0.9"")

    if [ ""$download_speed_mbps"" -ge ""$threshold"" ]; then
        # Within 10%: blend {withinRatio}
        blended_speed=$(num_i ""($baseline_speed * {withinBaseline} + $download_speed_mbps * {withinMeasured})"")
    else
        # Below 10%: favor baseline {belowRatio}
        blended_speed=$(num_i ""($baseline_speed * {belowBaseline} + $download_speed_mbps * {belowMeasured})"")
    fi

    download_speed_mbps=$(num_i ""$blended_speed * $DOWNLOAD_SPEED_MULTIPLIER"")
else
    download_speed_mbps=$(num_i ""$download_speed_mbps * $DOWNLOAD_SPEED_MULTIPLIER"")
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

        return $@"# Baseline blending for ping (with quarter-hour interpolation)
current_day=$(date +%u)
current_day=$((current_day - 1))
current_hour=$(date +%H | sed 's/^0//')
current_min=$(date +%M | sed 's/^0//')
lookup_key=""${{current_day}}_${{current_hour}}""

# Next hour lookup (wraps at midnight to next day)
next_hour=$(( (current_hour + 1) % 24 ))
if [ ""$next_hour"" -eq 0 ]; then
    next_day=$(( (current_day + 1) % 7 ))
else
    next_day=$current_day
fi
next_key=""${{next_day}}_${{next_hour}}""

baseline_speed=${{BASELINE[$lookup_key]}}
next_baseline_speed=${{BASELINE[$next_key]}}

# Interpolate at 15-minute breakpoints: :00=0%, :15=25%, :30=50%, :45=75%
if [ -n ""$baseline_speed"" ] && [ -n ""$next_baseline_speed"" ]; then
    quarter=$(( current_min / 15 ))
    weight=$(( quarter * 25 ))
    baseline_speed=$(( (baseline_speed * (100 - weight) + next_baseline_speed * weight) / 100 ))
    if [ ""$baseline_speed"" -lt 5 ]; then baseline_speed=5; fi
fi

if [ -n ""$baseline_speed"" ]; then
    baseline_with_overhead=$(num_i ""$baseline_speed * {overheadMultiplier}"")
    if [ ""$baseline_with_overhead"" -gt ""$MAX_DOWNLOAD_SPEED_CONFIG"" ]; then
        baseline_with_overhead=$MAX_DOWNLOAD_SPEED_CONFIG
    fi
    MAX_DOWNLOAD_SPEED=$(num_i ""($baseline_with_overhead * {baselineWeight} + $SPEEDTEST_SPEED * {measuredWeight})"")
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
if (( $(num_bool ""$latency >= $BASELINE_LATENCY + $LATENCY_THRESHOLD"") )); then
    # High latency: decrease rate with non-linear response ((n+1)^0.7 - 1)
    # Gentle at low deviations (transient spikes), aggressive at high (real congestion)
    effective_count=$(num_s 4 ""exp(0.7 * log($deviation_count + 1)) - 1"")
    decrease_multiplier=$(num_f ""exp($effective_count * log($LATENCY_DECREASE))"")
    new_rate=$(num_f ""$MAX_DOWNLOAD_SPEED * $decrease_multiplier"")
    if (( $(num_bool ""$new_rate < $MIN_DOWNLOAD_SPEED"") )); then
        new_rate=$MIN_DOWNLOAD_SPEED
    fi

elif (( $(num_bool ""$latency < $BASELINE_LATENCY - 0.4"") )); then
    # Low latency: can increase
    lower_bound=$(num_f ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.92"")
    mid_bound=$(num_f ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.94"")
    if (( $(num_bool ""$MAX_DOWNLOAD_SPEED < $lower_bound"") )); then
        new_rate=$(num_f ""$MAX_DOWNLOAD_SPEED * $LATENCY_INCREASE * $LATENCY_INCREASE"")
    elif (( $(num_bool ""$MAX_DOWNLOAD_SPEED < $mid_bound"") )); then
        new_rate=$mid_bound
    else
        new_rate=$MAX_DOWNLOAD_SPEED
    fi

else
    # Normal latency
    lower_bound=$(num_f ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.9"")
    mid_bound=$(num_f ""$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.92"")
    latency_diff=$(num_f ""$latency - $BASELINE_LATENCY"")
    latency_normal=$(num_bool ""$latency_diff <= 0.3"")

    if (( $(num_bool ""$MAX_DOWNLOAD_SPEED < $lower_bound"") )) && (( latency_normal == 1 )); then
        new_rate=$(num_f ""$MAX_DOWNLOAD_SPEED * $LATENCY_INCREASE"")
    elif (( $(num_bool ""$MAX_DOWNLOAD_SPEED < $mid_bound"") )) && (( latency_normal == 1 )); then
        new_rate=$mid_bound
    else
        new_rate=$MAX_DOWNLOAD_SPEED
    fi
fi";
    }
}
