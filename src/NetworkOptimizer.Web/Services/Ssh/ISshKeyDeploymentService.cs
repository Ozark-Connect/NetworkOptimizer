using System.Text;
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
    /// Removes the boot script, the stored copy of the key, and our line from authorized_keys. Other
    /// keys on the gateway are left alone.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, TargetType = "ssh_key_deployment")]
    Task<(bool success, string message)> RemoveAsync();

    /// <summary>
    /// Connects to the gateway offering ONLY the stored key, so success means the key itself
    /// authenticated. Normal connections offer the password too and fall back to it silently, which
    /// is what makes "is the key working?" unanswerable from a successful connection or from the logs.
    /// </summary>
    [RequireRole(Roles.Operator)]
    Task<(bool success, string message)> TestKeyAsync();
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

    private readonly IGatewaySshService _gatewaySsh;
    private readonly IUdmBootService _udmBoot;
    private readonly ISshKeyService _sshKeys;
    private readonly SshClientService _sshClient;
    private readonly ILogger<SshKeyDeploymentService> _logger;

    public SshKeyDeploymentService(
        IGatewaySshService gatewaySsh,
        IUdmBootService udmBoot,
        ISshKeyService sshKeys,
        SshClientService sshClient,
        ILogger<SshKeyDeploymentService> logger)
    {
        _gatewaySsh = gatewaySsh;
        _udmBoot = udmBoot;
        _sshKeys = sshKeys;
        _sshClient = sshClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(bool success, string message)> TestKeyAsync()
    {
        if (await _gatewaySsh.IsAwaitingAgentTunnelAsync())
            return (false, GatewaySshService.AwaitingAgentMessage);

        var connection = await _gatewaySsh.GetConnectionInfoAsync();
        if (connection is null)
            return (false, "Gateway SSH is not configured.");

        if (string.IsNullOrEmpty(connection.StoredPrivateKeyPem))
            return (false, "This site has no stored SSH key.");

        // Strip everything else off the connection: with the password still attached, a successful
        // connect proves nothing, because SSH.NET would fall back to it without saying so.
        connection.Password = null;
        connection.PrivateKeyPath = null;

        var (success, message) = await _sshClient.TestConnectionAsync(connection);
        _logger.LogInformation(
            "Stored SSH key test against the gateway: {Result}", success ? "authenticated" : "rejected");

        return success
            ? (true, "The gateway accepted the stored key.")
            : (false, $"The gateway did not accept the stored key: {message}");
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

        // base64 both payloads so no quoting or newline in a key or script can break the command,
        // the same way Performance Tweaks ships its scripts.
        var keyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.PublicKey + "\n"));
        var scriptB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(BootScript()));

        var deploy = await _gatewaySsh.RunCommandAsync(
            $"mkdir -p {KeyDir} && echo '{keyB64}' | base64 -d > {KeyPath} && chmod 600 {KeyPath} && "
            + $"echo '{scriptB64}' | base64 -d > {OnBootDir}/{ScriptName} && "
            + $"chmod +x {OnBootDir}/{ScriptName} && {OnBootDir}/{ScriptName} && echo DEPLOYED");

        if (!deploy.success || !deploy.output.Contains("DEPLOYED"))
        {
            _logger.LogWarning("SSH key deployment to gateway failed: {Output}", deploy.output);
            return (false, $"Could not place the key on the gateway: {deploy.output.Trim()}");
        }

        // Confirm from the gateway rather than trusting the exit code - the whole point is that the
        // key is actually usable now, not that a script ran.
        var verify = await _gatewaySsh.RunCommandAsync(
            $"grep -qF '{KeyBlob(key.PublicKey)}' /root/.ssh/authorized_keys && echo PRESENT || echo ABSENT");

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

        var key = await _sshKeys.GetAsync();
        var blob = KeyBlob(key?.PublicKey);
        if (blob is null)
            return (false, "There is no stored SSH key to remove.");

        var result = await _gatewaySsh.RunCommandAsync(
            $"rm -f {OnBootDir}/{ScriptName} {KeyPath}; "
            + $"if [ -f /root/.ssh/authorized_keys ]; then "
            + $"grep -vF '{blob}' /root/.ssh/authorized_keys > /tmp/netopt-ak.$$ || true; "
            + $"cat /tmp/netopt-ak.$$ > /root/.ssh/authorized_keys; rm -f /tmp/netopt-ak.$$; fi; echo REMOVED");

        if (!result.success || !result.output.Contains("REMOVED"))
            return (false, $"Could not remove the key from the gateway: {result.output.Trim()}");

        return (true, "Key removed from the gateway.");
    }

    /// <summary>
    /// Idempotent and deliberately non-destructive: it strips only the lines carrying this exact key
    /// and appends it once, so any other key an operator put on the gateway survives. Rebuilding
    /// authorized_keys wholesale would be simpler and would silently delete their access.
    /// </summary>
    private static string BootScript() => string.Join("\n", new[]
    {
        "#!/bin/sh",
        "# Network Optimizer - keep the managed SSH public key in root's authorized_keys.",
        "# Idempotent and non-destructive: it removes only lines carrying THIS key and appends it",
        "# once, so any other key an operator placed on the gateway survives untouched.",
        "",
        $"KEY_FILE={KeyPath}",
        "AUTH=/root/.ssh/authorized_keys",
        "",
        "[ -f \"$KEY_FILE\" ] || exit 0",
        "",
        "# Match on the key material, not the comment: the same key may already be present under a",
        "# comment its owner chose, and appending ours would leave it in the file twice.",
        "BLOB=$(awk '{print $2}' \"$KEY_FILE\")",
        "[ -n \"$BLOB\" ] || exit 0",
        "",
        "mkdir -p /root/.ssh",
        "chmod 700 /root/.ssh",
        "[ -f \"$AUTH\" ] || : > \"$AUTH\"",
        "",
        "TMP=\"$AUTH.netopt.$$\"",
        "grep -vF \"$BLOB\" \"$AUTH\" > \"$TMP\" 2>/dev/null || true",
        "cat \"$KEY_FILE\" >> \"$TMP\"",
        "cat \"$TMP\" > \"$AUTH\"",
        "rm -f \"$TMP\"",
        "chmod 600 \"$AUTH\"",
        "",
        "echo \"Network Optimizer SSH key placed in $AUTH\"",
        "",
    });
}
