using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>A UniFi device to reach over SSH.</summary>
/// <param name="Mac">The device's MAC, in any case or separator form.</param>
/// <param name="Host">The address to dial. Unused for the site gateway, which has its own configured host.</param>
/// <param name="Role">The role UniFi Network gives the device (<see cref="DiscoveredDevice.Type"/>).</param>
/// <param name="HardwareType">The hardware class, or null to look it up from the site's device list by MAC.</param>
public sealed record DeviceSshTarget(string Mac, string Host, DeviceType Role, DeviceType? HardwareType = null)
{
    /// <summary>The target for a discovered device, at <paramref name="host"/> or its display address.</summary>
    public static DeviceSshTarget From(DiscoveredDevice device, string? host = null) =>
        new(device.Mac, host ?? device.DisplayIpAddress, device.Type, device.HardwareType);
}

/// <summary>Which credentials, at which host, a device's SSH session uses.</summary>
public enum SshCredentialRoute
{
    /// <summary>Device SSH credentials, at the device.</summary>
    DeviceCredentials,

    /// <summary>The site gateway, through the Gateway SSH service and its configured host.</summary>
    Gateway,

    /// <summary>Gateway SSH credentials at the device's own address: gateway hardware in AP mode.</summary>
    GatewayCredentialsAtDevice,
}

/// <summary>Gateway SSH credentials to apply as per-device overrides. The password is encrypted, as stored.</summary>
public sealed record DeviceSshCredentialOverrides(string? Username, string? Password, string? PrivateKeyPath);

/// <summary>
/// The one place that decides which SSH credentials a UniFi device takes, and at which host.
///
/// The site gateway goes through the Gateway SSH service. Every other device takes Device SSH,
/// except gateway hardware adopted as an access point (UX, UX7): UniFi OS may refuse the
/// adopted-device login there, so a refused Device SSH login is retried with the Gateway SSH
/// credentials at the device's own address. The outcome is persisted per MAC, so a restart does
/// not cost a refused login per device, and dropped again if the Gateway SSH login is refused.
/// </summary>
public sealed class DeviceSshRouter
{
    internal const string GatewayCredentialsMissing = "Gateway SSH credentials are not configured for this site.";

    /// <summary>Bounds the no-op login that settles a route before a caller needs its credentials.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(15);

    private readonly IUniFiSshService _deviceSsh;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly IDeviceSshRouteStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> _devices;
    private readonly Func<SshConnectionInfo, Task<SshConnectionInfo?>> _prepareConnection;
    private readonly Func<string, string> _decrypt;
    private readonly ILogger _logger;

    /// <summary>Creates the router for one site.</summary>
    /// <param name="devices">The site's device list, used only to find a target's hardware class.</param>
    /// <param name="prepareConnection">Attaches the site's stored key and routes through the site's
    /// agent tunnel; returns null when the connection has no credentials at all.</param>
    /// <param name="decrypt">Decrypts a stored password.</param>
    public DeviceSshRouter(
        IUniFiSshService deviceSsh,
        IGatewaySshService gatewaySsh,
        IDeviceSshRouteStore store,
        Func<CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> devices,
        Func<SshConnectionInfo, Task<SshConnectionInfo?>> prepareConnection,
        Func<string, string> decrypt,
        ILogger logger)
    {
        _deviceSsh = deviceSsh;
        _gatewaySsh = gatewaySsh;
        _store = store;
        _devices = devices;
        _prepareConnection = prepareConnection;
        _decrypt = decrypt;
        _logger = logger;
    }

    /// <summary>The route the device takes now, from what is already known. Never dials.</summary>
    public async Task<SshCredentialRoute> ResolveAsync(DeviceSshTarget target, CancellationToken ct = default)
    {
        if (target.Role == DeviceType.Gateway) return SshCredentialRoute.Gateway;
        if (!await IsGatewayHardwareAsync(target, ct)) return SshCredentialRoute.DeviceCredentials;
        return await _store.UsesGatewayCredentialsAsync(NormalizeMac(target.Mac), ct)
            ? SshCredentialRoute.GatewayCredentialsAtDevice
            : SshCredentialRoute.DeviceCredentials;
    }

    /// <summary>
    /// Runs a command on the device. A null timeout takes the SSH service's own default.
    /// </summary>
    public async Task<(bool success, string output)> RunAsync(
        DeviceSshTarget target, string command, TimeSpan? timeout, CancellationToken ct = default)
    {
        if (target.Role == DeviceType.Gateway)
            return await _gatewaySsh.RunCommandAsync(command, timeout, ct);

        var gatewayHardware = await IsGatewayHardwareAsync(target, ct);
        var mac = NormalizeMac(target.Mac);

        if (gatewayHardware && await _store.UsesGatewayCredentialsAsync(mac, ct))
        {
            var remembered = await RunWithGatewayCredentialsAsync(target.Host, command, timeout, ct);
            if (!IsCredentialRefusal(remembered.output)) return remembered;

            await _store.SetUsesGatewayCredentialsAsync(mac, false, ct);
            _logger.LogInformation("{Host} ({Mac}) refused Gateway SSH; back to Device SSH", target.Host, mac);
            return await RunWithDeviceCredentialsAsync(target.Host, command, timeout, ct);
        }

        var result = await RunWithDeviceCredentialsAsync(target.Host, command, timeout, ct);
        if (result.success || !gatewayHardware || !IsCredentialRefusal(result.output)) return result;

        var fallback = await RunWithGatewayCredentialsAsync(target.Host, command, timeout, ct);
        if (IsCredentialRefusal(fallback.output)) return result;

        await _store.SetUsesGatewayCredentialsAsync(mac, true, ct);
        _logger.LogInformation("{Host} ({Mac}) refused Device SSH and accepted Gateway SSH; using Gateway SSH from now on",
            target.Host, mac);
        return fallback;
    }

    /// <summary>
    /// The Gateway SSH credentials to set as per-device overrides, or null when the device takes
    /// Device SSH. Settles the route with a no-op login first when the device is gateway hardware in
    /// AP mode and nothing is remembered yet, so the answer reflects what the device accepts.
    /// </summary>
    public async Task<DeviceSshCredentialOverrides?> GetCredentialOverridesAsync(
        DeviceSshTarget target, CancellationToken ct = default)
    {
        if (target.Role == DeviceType.Gateway || !await IsGatewayHardwareAsync(target, ct)) return null;

        if (await ResolveAsync(target, ct) == SshCredentialRoute.DeviceCredentials)
            await RunAsync(target, "true", SettleTimeout, ct);

        if (await ResolveAsync(target, ct) != SshCredentialRoute.GatewayCredentialsAtDevice) return null;

        var gateway = await _gatewaySsh.GetSettingsAsync();
        return gateway.Enabled
            ? new DeviceSshCredentialOverrides(gateway.Username, gateway.Password, gateway.PrivateKeyPath)
            : null;
    }

    /// <summary>
    /// A ready connection for direct SSH.NET work (file transfer), on the route the device takes now.
    /// Null when that route has no credentials configured.
    /// </summary>
    public async Task<SshConnectionInfo?> GetConnectionAsync(DeviceSshTarget target, CancellationToken ct = default)
    {
        var route = await ResolveAsync(target, ct);
        if (route == SshCredentialRoute.Gateway)
            return await _gatewaySsh.GetConnectionInfoAsync();

        var device = await _deviceSsh.GetSettingsAsync();
        var username = device.Username;
        var password = device.Password;
        var keyPath = device.PrivateKeyPath;

        if (route == SshCredentialRoute.GatewayCredentialsAtDevice)
        {
            var gateway = await _gatewaySsh.GetSettingsAsync();
            if (!gateway.Enabled) return null;
            // Same precedence as the command path's overrides: a gateway value wins where it is set.
            if (!string.IsNullOrEmpty(gateway.Username)) username = gateway.Username;
            if (!string.IsNullOrEmpty(gateway.Password)) password = gateway.Password;
            if (!string.IsNullOrEmpty(gateway.PrivateKeyPath)) keyPath = gateway.PrivateKeyPath;
        }

        if (string.IsNullOrEmpty(username)) return null;

        return await _prepareConnection(new SshConnectionInfo
        {
            Host = target.Host,
            Port = device.Port,
            Username = username,
            Password = string.IsNullOrEmpty(password) ? null : _decrypt(password),
            PrivateKeyPath = keyPath,
            Timeout = TimeSpan.FromSeconds(5),
        });
    }

    /// <summary>A login the credentials could not make, as opposed to a host that could not be reached.</summary>
    internal static bool IsCredentialRefusal(string output) =>
        output.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("credentials not configured", StringComparison.OrdinalIgnoreCase) ||
        output == GatewayCredentialsMissing;

    private Task<(bool success, string output)> RunWithDeviceCredentialsAsync(
        string host, string command, TimeSpan? timeout, CancellationToken ct) =>
        timeout is { } t
            ? _deviceSsh.RunCommandAsync(host, command, null, t, ct)
            : _deviceSsh.RunCommandAsync(host, command, null, ct);

    /// <summary>Gateway SSH credentials, dialed at the device's own address and Device SSH port.</summary>
    private async Task<(bool success, string output)> RunWithGatewayCredentialsAsync(
        string host, string command, TimeSpan? timeout, CancellationToken ct)
    {
        var gateway = await _gatewaySsh.GetSettingsAsync();
        if (!gateway.Enabled || !gateway.HasCredentials)
            return (false, GatewayCredentialsMissing);

        return await _deviceSsh.RunCommandAsync(
            host, command, gateway.Username, gateway.Password, gateway.PrivateKeyPath, timeout, ct);
    }

    private async Task<bool> IsGatewayHardwareAsync(DeviceSshTarget target, CancellationToken ct)
    {
        if (target.HardwareType is { } known) return known == DeviceType.Gateway;

        var mac = NormalizeMac(target.Mac);
        var devices = await _devices(ct);
        return devices.FirstOrDefault(d => NormalizeMac(d.Mac) == mac)?.HardwareType == DeviceType.Gateway;
    }

    internal static string NormalizeMac(string mac) => mac.Trim().Replace('-', ':').ToLowerInvariant();
}
