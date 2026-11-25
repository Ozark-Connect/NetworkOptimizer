using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// What the gateway currently has, so the panel can say "install" or "installed" rather than guess.
/// </summary>
/// <param name="GatewayConfigured">Gateway SSH is enabled and has credentials to deploy with.</param>
/// <param name="UdmBootInstalled">The shared udm-boot unit is present.</param>
/// <param name="ScriptInstalled">Our boot script is on the gateway.</param>
/// <param name="KeyInstalled">The public key is currently in root's authorized_keys.</param>
/// <param name="AwaitingAgent">
/// The gateway is reached through this site's agent and the agent is not online, so nothing was asked
/// of it. Nothing here is known yet; the caller should ask again rather than cache this.
/// </param>
public sealed record SshKeyDeploymentStatus(
    bool GatewayConfigured, bool UdmBootInstalled, bool ScriptInstalled, bool KeyInstalled,
    bool AwaitingAgent = false)
{
    /// <summary>True when the key is placed and will survive a reboot or firmware upgrade.</summary>
    public bool FullyDeployed => ScriptInstalled && KeyInstalled;
}

/// <summary>Messages callers of <see cref="ISshKeyDeploymentService"/> compare against.</summary>
public static class SshKeyDeploymentMessages
{
    /// <summary>Remove's message when the key was taken out of authorized_keys; any other success kept it.</summary>
    public const string Removed = "Key removed from the gateway.";
}

/// <summary>
/// Places the site's stored public key in the gateway's <c>authorized_keys</c> and keeps it there
/// across reboots and firmware upgrades, using the same udm-boot mechanism Adaptive SQM, WAN Steering
/// and Performance Tweaks already deploy through.
///
/// This exists because a Cloud Gateway is its own console, so UniFi Network's Device SSH Settings does
/// not reach its root SSH - without this the first placement is a manual SSH session, and every
/// firmware upgrade may undo it. With a working password already configured, upgrading that gateway
/// from password to key authentication becomes two clicks.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISshKeyDeploymentService
{
    /// <summary>What the gateway currently has. Viewer-level: the panel shows this as state.</summary>
    [RequireRole(Roles.Viewer)]
    Task<SshKeyDeploymentStatus> GetStatusAsync();

    /// <summary>
    /// Installs udm-boot if absent, writes the key and the boot script, then runs the script so the
    /// key is placed now rather than at the next reboot.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, TargetType = "ssh_key_deployment")]
    Task<(bool success, string message)> DeployAsync();

    /// <summary>
    /// Removes the boot script and the stored copy of the key, and takes the key out of authorized_keys
    /// only when Network Optimizer put it there. A key that was already on the gateway before the first
    /// install, or that Network Optimizer never placed, stays: removing it would lock its owner out.
    /// Other keys on the gateway are left alone.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, TargetType = "ssh_key_deployment")]
    Task<(bool success, string message)> RemoveAsync();
}

/// <inheritdoc />
public sealed class SshKeyDeploymentService : ISshKeyDeploymentService
{
    private const string OnBootDir = "/data/on_boot.d";

    /// <summary>
    /// 26 sits clear of everything we ship (06, 07, 10, 15, 19, 20, 21 and WAN Steering's 25). Nothing
    /// here depends on ordering; the number only needs to not collide.
    /// </summary>
    private const string ScriptName = "26-netopt-ssh-key.sh";

    private const string KeyDir = "/data/netopt";
    private const string KeyPath = KeyDir + "/ssh-key.pub";

    /// <summary>
    /// Holds the blob of a key that was already in authorized_keys when Network Optimizer first
    /// installed it. Remove leaves that key in place: it was the operator's before it was ours.
    /// </summary>
    private const string PreexistingPath = KeyDir + "/ssh-key.preexisting";

    private const string AuthPath = "/root/.ssh/authorized_keys";

    /// <summary>The gateway paths the shell commands work on. Tests point them at a temp directory.</summary>
    internal sealed record GatewayPaths(string OnBootDir, string KeyPath, string PreexistingPath, string AuthPath)
    {
        public static readonly GatewayPaths Gateway = new(
            SshKeyDeploymentService.OnBootDir, SshKeyDeploymentService.KeyPath,
            SshKeyDeploymentService.PreexistingPath, SshKeyDeploymentService.AuthPath);

        public string ScriptPath => $"{OnBootDir}/{ScriptName}";

        /// <summary>The directory part of a gateway path. Not Path.GetDirectoryName: the server may be Windows.</summary>
        public static string DirOf(string path) => path[..path.LastIndexOf('/')];
    }

    private readonly IGatewaySshService _gatewaySsh;
    private readonly IUdmBootService _udmBoot;
    private readonly ISshKeyService _sshKeys;
    private readonly ILogger<SshKeyDeploymentService> _logger;

    public SshKeyDeploymentService(
        IGatewaySshService gatewaySsh,
        IUdmBootService udmBoot,
        ISshKeyService sshKeys,
        ILogger<SshKeyDeploymentService> logger)
    {
        _gatewaySsh = gatewaySsh;
        _udmBoot = udmBoot;
        _sshKeys = sshKeys;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SshKeyDeploymentStatus> GetStatusAsync()
    {
        var settings = await _gatewaySsh.GetSettingsAsync();
        if (!CanReachGatewayToday(settings))
            return new SshKeyDeploymentStatus(false, false, false, false);

        // Dialing now would just hit the refusing tunnel proxy, which SSH.NET reports as a raw
        // protocol error - the same reason every other gateway caller checks this first.
        if (await _gatewaySsh.IsAwaitingAgentTunnelAsync())
            return new SshKeyDeploymentStatus(true, false, false, false, AwaitingAgent: true);

        // Match on the key blob, not our comment. An uploaded key that is already on the gateway
        // carries whatever comment its owner gave it, so a comment match would report "not installed"
        // and nag someone to install a key that already works.
        var key = await _sshKeys.GetAsync();
        var blob = KeyBlob(key?.PublicKey);

        var probe = await _gatewaySsh.RunCommandAsync(
            $"test -f /etc/systemd/system/udm-boot.service && echo UDMBOOT; " +
            $"test -f {OnBootDir}/{ScriptName} && echo SCRIPT; " +
            (blob is null
                ? ""
                : $"grep -qF '{blob}' /root/.ssh/authorized_keys 2>/dev/null && echo KEY; ") +
            "true");

        if (!probe.success)
            return new SshKeyDeploymentStatus(true, false, false, false);

        return new SshKeyDeploymentStatus(
            true,
            probe.output.Contains("UDMBOOT"),
            probe.output.Contains("SCRIPT"),
            probe.output.Contains("KEY"));
    }



    /// <summary>
    /// The base64 middle field of an OpenSSH public key line - the key itself, without the algorithm
    /// prefix or the comment. This is what identifies a key across machines; base64 contains no quote
    /// characters, so it is safe to embed in a single-quoted shell argument.
    /// </summary>
    private static string? KeyBlob(string? publicKeyLine)
    {
        var parts = publicKeyLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 } ? parts[1] : null;
    }

    /// <summary>
    /// Whether we can log in to the gateway RIGHT NOW to place the key. Deliberately not
    /// <c>HasCredentials</c>, which counts the stored key: that is the key we are trying to install,
    /// so it cannot be what gets us in. A password or an existing key file is what works today.
    /// </summary>
    private static bool CanReachGatewayToday(Storage.Models.GatewaySshSettings settings)
        => settings.Enabled
            && !string.IsNullOrEmpty(settings.Host)
            && (!string.IsNullOrEmpty(settings.Password) || !string.IsNullOrEmpty(settings.PrivateKeyPath));

    /// <inheritdoc />
    public async Task<(bool success, string message)> DeployAsync()
    {
        var key = await _sshKeys.GetAsync();
        if (key is null)
            return (false, "Generate or upload an SSH key first.");
        var blob = KeyBlob(key.PublicKey);
        if (blob is null)
            return (false, "The stored SSH key is not a valid OpenSSH public key.");

        var settings = await _gatewaySsh.GetSettingsAsync();
        if (!CanReachGatewayToday(settings))
            return (false, "Configure Gateway SSH with a password first - the key is placed over the existing connection.");

        if (await _gatewaySsh.IsAwaitingAgentTunnelAsync())
            return (false, GatewaySshService.AwaitingAgentMessage);

        if (!await _udmBoot.IsInstalledAsync())
        {
            var (bootOk, bootMessage) = await _udmBoot.InstallAsync();
            if (!bootOk)
                return (false, $"Could not install udm-boot on the gateway: {bootMessage}");
        }

        var deploy = await _gatewaySsh.RunCommandAsync(DeployCommand(GatewayPaths.Gateway, key.PublicKey, blob));

        if (!deploy.success || !deploy.output.Contains("DEPLOYED"))
        {
            _logger.LogWarning("SSH key deployment to gateway failed: {Output}", deploy.output);
            return (false, $"Could not place the key on the gateway: {deploy.output.Trim()}");
        }

        // Confirm from the gateway rather than trusting the exit code - the whole point is that the
        // key is actually usable now, not that a script ran.
        var verify = await _gatewaySsh.RunCommandAsync(
            $"grep -qF '{blob}' {AuthPath} && echo PRESENT || echo ABSENT");

        if (!verify.success || !verify.output.Contains("PRESENT"))
            return (false, "The script ran but the key is not in authorized_keys. Check the gateway logs.");

        _logger.LogInformation("Placed the site's SSH public key on gateway {Host}", settings.Host);
        return (true, "Key installed on the gateway. It will be replaced after reboots and firmware upgrades.");
    }

    /// <inheritdoc />
    public async Task<(bool success, string message)> RemoveAsync()
    {
        if (await _gatewaySsh.IsAwaitingAgentTunnelAsync())
            return (false, GatewaySshService.AwaitingAgentMessage);

        var result = await _gatewaySsh.RunCommandAsync(RemoveCommand(GatewayPaths.Gateway));

        if (!result.success || result.output.Contains("FAILED"))
            return (false, $"Could not remove the key from the gateway: {result.output.Trim()}");
        if (result.output.Contains("KEPT_PREEXISTING"))
            return (true, "Network Optimizer no longer keeps the key on the gateway. It was there before we installed it, so it stays in authorized_keys.");
        if (result.output.Contains("NOT_PLACED"))
            return (true, "Network Optimizer did not put this key on the gateway, so it stays in authorized_keys.");
        if (result.output.Contains("REMOVED"))
            return (true, SshKeyDeploymentMessages.Removed);
        return (false, $"Could not remove the key from the gateway: {result.output.Trim()}");
    }

    /// <summary>
    /// Writes the key and the boot script, then runs the script. Before the first placement of a key it
    /// records whether that key is already in authorized_keys, so Remove can leave the operator's own
    /// copy alone. Re-deploying the same key keeps the record from its first placement; a gateway
    /// deployed before the record existed has none, and Remove treats its key as ours, as it always did.
    /// </summary>
    internal static string DeployCommand(GatewayPaths p, string publicKeyLine, string blob)
    {
        // base64 both payloads so no quoting or newline in a key or script can break the command,
        // the same way Performance Tweaks ships its scripts. The blob is base64 itself: no quotes.
        var keyB64 = GatewayFile.ToBase64(publicKeyLine.Trim() + "\n");
        var scriptB64 = GatewayFile.ToBase64(BootScript(p));
        var keyDir = GatewayPaths.DirOf(p.KeyPath);
        return $"NEW='{blob}'; OLD=''; [ -f '{p.KeyPath}' ] && OLD=$(awk '{{print $2}}' '{p.KeyPath}'); "
            + $"if [ \"$OLD\" != \"$NEW\" ]; then rm -f '{p.PreexistingPath}'; "
            + $"if [ -f '{p.AuthPath}' ] && grep -qF \"$NEW\" '{p.AuthPath}'; then mkdir -p '{keyDir}' && printf '%s\\n' \"$NEW\" > '{p.PreexistingPath}'; fi; fi; "
            + $"mkdir -p '{keyDir}' && echo '{keyB64}' | base64 -d > '{p.KeyPath}' && chmod 600 '{p.KeyPath}' && "
            + $"echo '{scriptB64}' | base64 -d > '{p.ScriptPath}' && "
            + $"chmod +x '{p.ScriptPath}' && '{p.ScriptPath}' && echo DEPLOYED";
    }

    /// <summary>
    /// Undoes what Network Optimizer did and nothing more. The key taken out of authorized_keys is the
    /// one in the stored copy on the gateway (what we placed), not the site's current key, and only
    /// when it was not already there before we placed it. Without a stored copy nothing is taken out:
    /// a key we never placed is someone's own access. Prints REMOVED, KEPT_PREEXISTING, or NOT_PLACED.
    /// </summary>
    internal static string RemoveCommand(GatewayPaths p) =>
        $"rm -f '{p.ScriptPath}'; PLACED=''; [ -f '{p.KeyPath}' ] && PLACED=$(awk '{{print $2}}' '{p.KeyPath}'); "
        + $"PRE=''; [ -f '{p.PreexistingPath}' ] && PRE=$(head -n 1 '{p.PreexistingPath}'); "
        + "if [ -z \"$PLACED\" ]; then RESULT=NOT_PLACED; "
        + "elif [ \"$PLACED\" = \"$PRE\" ]; then RESULT=KEPT_PREEXISTING; "
        + $"else RESULT=REMOVED; if [ -f '{p.AuthPath}' ]; then "
        // grep exits 0 or 1 with a complete copy (1: every line went); 2 is an error, such as a full disk,
        // and its partial copy must never replace authorized_keys - that would lock every key out.
        + $"grep -vF \"$PLACED\" '{p.AuthPath}' > '{p.AuthPath}.netopt.'$$; RC=$?; "
        + $"if [ $RC -le 1 ]; then cat '{p.AuthPath}.netopt.'$$ > '{p.AuthPath}'; else RESULT=FAILED; fi; "
        + $"rm -f '{p.AuthPath}.netopt.'$$; fi; fi; "
        + "if [ \"$RESULT\" != FAILED ]; then rm -f "
        + $"'{p.KeyPath}' '{p.PreexistingPath}'; fi; echo $RESULT";

    /// <summary>
    /// Idempotent and deliberately non-destructive: it appends the key only when authorized_keys does not
    /// already carry it, and never rewrites or removes a line. An operator's own copy of the same key,
    /// under their own comment, survives; stripping and re-adding it would replace their line with ours,
    /// and a later Remove would then take their access with it.
    /// </summary>
    internal static string BootScript(GatewayPaths p)
    {
        var authDir = GatewayPaths.DirOf(p.AuthPath);
        return string.Join("\n", new[]
        {
            "#!/bin/sh",
            "# Network Optimizer - keep the managed SSH public key in root's authorized_keys.",
            "# Non-destructive: it only appends the key when it is missing, and never rewrites or",
            "# removes a line, so every key an operator placed on the gateway survives untouched.",
            "",
            $"KEY_FILE='{p.KeyPath}'",
            $"AUTH='{p.AuthPath}'",
            "",
            "[ -f \"$KEY_FILE\" ] || exit 0",
            "",
            "# Match on the key material, not the comment: the same key may already be present under a",
            "# comment its owner chose.",
            "BLOB=$(awk '{print $2}' \"$KEY_FILE\")",
            "[ -n \"$BLOB\" ] || exit 0",
            "",
            $"mkdir -p '{authDir}'",
            $"chmod 700 '{authDir}'",
            "[ -f \"$AUTH\" ] || : > \"$AUTH\"",
            "",
            "if ! grep -qF \"$BLOB\" \"$AUTH\"; then",
            "    # A last line without a newline would join our key onto it.",
            "    if [ -s \"$AUTH\" ] && [ \"$(tail -c 1 \"$AUTH\" | wc -l)\" -eq 0 ]; then echo >> \"$AUTH\"; fi",
            "    cat \"$KEY_FILE\" >> \"$AUTH\"",
            "fi",
            "chmod 600 \"$AUTH\"",
            "",
            "echo \"Network Optimizer SSH key placed in $AUTH\"",
            "",
        });
    }
}
