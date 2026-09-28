using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.CableModemProviders.Uci;

/// <summary>
/// Server side of UniFi Cable Internet (UCI) monitoring. On-gateway agents relay the raw inform
/// frames each UCI sends through the gateway; this decrypts them with the device's inform key
/// (from the site's UniFi Console, never stored), keeps the latest decoded payload per UCI, and
/// tells each capable agent which UCI MACs to capture. The cable modem provider reads the
/// latest payload from here on its normal poll, so everything downstream (Influx, alerts, the
/// CM Stats UI) is the same path every other provider takes.
/// </summary>
public sealed partial class UciInformService
{
    /// <summary>Provider key of the UCI cable modem provider.</summary>
    public const string ProviderKey = "unifi-uci";

    /// <summary>A payload older than this is no longer current; the UCI informs about once a minute.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>Floor between forced key refreshes per site, so a bad frame cannot hammer the console.</summary>
    private static readonly TimeSpan KeyRefreshMinInterval = TimeSpan.FromMinutes(1);

    private readonly AgentTunnelRegistry _tunnels;
    private readonly SiteConnectionRegistry _connections;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<UciInformService> _logger;

    private readonly ConcurrentDictionary<(string Site, string Mac), Snapshot> _snapshots = new();
    private readonly ConcurrentDictionary<(string Site, string Mac), byte[]> _keys = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastKeyRefresh = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _captureTargets = new();
    private readonly ConcurrentDictionary<(string Site, string Mac), (string Reason, DateTime At)> _lastFailure = new();
    private readonly ConcurrentDictionary<(string Site, string Mac), bool> _sampleLogged = new();

    /// <summary>The latest decoded inform from one UCI.</summary>
    public sealed record Snapshot(UciInformPayload Payload, DateTime ReceivedAt);

    public UciInformService(
        AgentTunnelRegistry tunnels,
        SiteConnectionRegistry connections,
        SiteDbContextFactory siteDbFactory,
        ILogger<UciInformService> logger)
    {
        _tunnels = tunnels;
        _connections = connections;
        _siteDbFactory = siteDbFactory;
        _logger = logger;
    }

    /// <summary>Lowercase colon form, or null when the text is not a MAC.</summary>
    public static string? NormalizeMac(string? mac)
    {
        var bytes = NetworkOptimizer.Monitoring.UciInform.UciBpfFilter.ParseMac(mac);
        return bytes == null ? null : NetworkOptimizer.Monitoring.UciInform.UciBpfFilter.FormatMac(bytes);
    }

    /// <summary>The latest decoded inform for a UCI, or null when none has arrived since startup.</summary>
    public Snapshot? GetSnapshot(string siteSlug, string mac)
    {
        var key = NormalizeMac(mac);
        return key != null && _snapshots.TryGetValue((siteSlug, key), out var snapshot) ? snapshot : null;
    }

    /// <summary>Whether a connected agent of this site can capture UCI informs (on the gateway, recent enough).</summary>
    public bool HasCapableAgent(string siteSlug) =>
        _tunnels.GetForSite(siteSlug).Any(a => a.HasCapability(AgentTunnelConnection.UciInformCapability));

    /// <summary>
    /// Why there is no current payload for a UCI, in words a user can act on. Checked in order of
    /// what would have to be fixed first: the agent, then the frames, then time.
    /// </summary>
    public string DescribeMissing(string siteSlug, string mac)
    {
        var agents = _tunnels.GetForSite(siteSlug);
        if (agents.Count == 0)
            return "UCI stats need the On-Site Agent on the Gateway. No On-Site Agent is connected for this site.";
        if (!agents.Any(a => a.HasCapability(AgentTunnelConnection.UciInformCapability)))
            return "UCI stats need the On-Site Agent on the Gateway itself, at the current version. Install or update it on the Gateway.";

        var key = NormalizeMac(mac);
        if (key != null && _lastFailure.TryGetValue((siteSlug, key), out var failure)
            && DateTime.UtcNow - failure.At < StaleAfter)
            return failure.Reason;

        var snapshot = key != null ? GetSnapshot(siteSlug, key) : null;
        if (snapshot != null)
            return $"No update from the UCI for {(int)(DateTime.UtcNow - snapshot.ReceivedAt).TotalMinutes} minutes.";
        return "Waiting for the first update from the UCI (it sends one about every minute).";
    }

    /// <summary>
    /// Takes a relayed inform frame from the tunnel read loop and handles it in the background.
    /// Never await the handling there: decoding can need the site's console, which on an agent site
    /// is reached through that same tunnel, so the read loop would be waiting on a reply it cannot read.
    /// </summary>
    public void Accept(AgentTunnelConnection connection, UciInformFrame frame)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await HandleFrameAsync(connection, frame, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle a UCI inform from agent {Id}", connection.AgentId);
            }
        });
    }

    /// <summary>
    /// Handles one relayed inform frame: checks it came from a UCI this site monitors, decrypts it,
    /// and keeps the payload as that UCI's latest.
    /// </summary>
    public async Task HandleFrameAsync(AgentTunnelConnection connection, UciInformFrame frame, CancellationToken ct)
    {
        var site = connection.SiteSlug;
        var bytes = frame.Frame.ToByteArray();
        var header = UciInformDecoder.ParseHeader(bytes);
        var sourceMac = NormalizeMac(frame.SourceMac);
        if (header == null || sourceMac == null)
        {
            _logger.LogDebug("Agent {Id} relayed a UCI inform that is not a TNBU frame", connection.AgentId);
            return;
        }
        if (!string.Equals(header.Mac, sourceMac, StringComparison.Ordinal))
        {
            // The header names the sender; a frame whose header disagrees with the wire is not the
            // UCI's own, whatever the capture thought.
            _logger.LogDebug("UCI inform header MAC {Header} does not match its source {Source}; dropped", header.Mac, sourceMac);
            return;
        }

        var targets = await GetCaptureTargetsAsync(site, ct);
        if (!targets.Contains(sourceMac))
            return;

        var key = await GetKeyAsync(site, sourceMac, force: false, ct);
        if (key == null)
        {
            RecordFailure(site, sourceMac, "The UniFi Console did not return this UCI's inform key. " +
                "Check that the UCI is adopted and the UniFi Console connection is working.");
            return;
        }

        var result = UciInformDecoder.TryDecode(bytes, key, out var json);
        if (result == UciDecodeFailure.KeyMismatch)
        {
            // Re-adoption rotates the key; fetch it once and retry.
            var fresh = await GetKeyAsync(site, sourceMac, force: true, ct);
            if (fresh != null && !fresh.AsSpan().SequenceEqual(key))
                result = UciInformDecoder.TryDecode(bytes, fresh, out json);
        }

        switch (result)
        {
            case UciDecodeFailure.None:
                break;
            case UciDecodeFailure.KeyMismatch:
                RecordFailure(site, sourceMac, "The UCI's informs do not match the inform key from the UniFi Console. " +
                    "After a re-adoption this clears within a few minutes.");
                return;
            case UciDecodeFailure.Unsupported:
                RecordFailure(site, sourceMac, $"This UCI firmware uses an inform encoding Network Optimizer does not read yet (flags 0x{header.Flags:x2}).");
                return;
            default:
                RecordFailure(site, sourceMac, "A captured UCI inform could not be decoded.");
                return;
        }

        var payload = UciInformPayload.Parse(json);
        if (payload == null)
        {
            RecordFailure(site, sourceMac, "A decrypted UCI inform was not in the expected format.");
            return;
        }

        _snapshots[(site, sourceMac)] = new Snapshot(payload, DateTime.UtcNow);
        _lastFailure.TryRemove((site, sourceMac), out _);

        if (_sampleLogged.TryAdd((site, sourceMac), true))
        {
            // One redacted sample per UCI per process: the field names a support thread needs,
            // without the identifiers a public issue should not carry.
            _logger.LogInformation(
                "First UCI inform decoded for site {Site} (flags 0x{Flags:x2}, {Bytes} bytes). Redacted payload: {Payload}",
                site, header.Flags, bytes.Length, Redact(Encoding.UTF8.GetString(json)));
        }
    }

    /// <summary>Pushes this site's UCI capture targets to one agent, when it can capture.</summary>
    public async Task PushCaptureConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        if (!connection.HasCapability(AgentTunnelConnection.UciInformCapability)) return;
        try
        {
            var targets = await LoadCaptureTargetsAsync(connection.SiteSlug, ct);
            var config = new UciCaptureConfig();
            config.Macs.AddRange(targets.OrderBy(m => m, StringComparer.Ordinal));
            connection.TrySend(new ServerMessage { UciCaptureConfig = config });
            _logger.LogDebug("Pushed UCI capture config ({Count} UCI) to agent {Id} (site {Slug})",
                config.Macs.Count, connection.AgentId, connection.SiteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push UCI capture config to agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
        }
    }

    /// <summary>Re-pushes capture targets to every capable agent of a site, after its UCI configs change.</summary>
    public async Task PushCaptureConfigToSiteAsync(string siteSlug, CancellationToken ct = default)
    {
        _captureTargets.TryRemove(siteSlug, out _);
        foreach (var connection in _tunnels.GetForSite(siteSlug))
            await PushCaptureConfigAsync(connection, ct);
    }

    /// <summary>
    /// The site's adopted UCIs that have no cable modem config yet and were never deleted by the
    /// user, as (MAC, UniFi name) pairs. Reads the console's cached device list, and refreshes the
    /// inform keys from it while it is there.
    /// </summary>
    public async Task<IReadOnlyList<(string Mac, string Name)>> FindUnconfiguredUcisAsync(
        string siteSlug, IEnumerable<string> configuredHosts, CancellationToken ct = default)
    {
        var ucis = await ListUcisAsync(siteSlug, ct);
        if (ucis.Count == 0) return ucis;

        var configured = configuredHosts.Select(NormalizeMac).Where(m => m != null).ToHashSet(StringComparer.Ordinal);
        var dismissed = await LoadDismissedAsync(siteSlug, ct);
        if (dismissed == null) return Array.Empty<(string, string)>();
        return ucis.Where(u => !configured.Contains(u.Mac) && !dismissed.Contains(u.Mac)).ToList();
    }

    /// <summary>
    /// The site's adopted UCIs as (MAC, UniFi name) pairs, from the console's cached device list.
    /// Refreshes the inform keys from that list while it is there.
    /// </summary>
    public async Task<IReadOnlyList<(string Mac, string Name)>> ListUcisAsync(string siteSlug, CancellationToken ct = default)
    {
        var client = _connections.GetFor(siteSlug).Client;
        if (client == null) return Array.Empty<(string, string)>();

        List<UniFiDeviceResponse> devices;
        try
        {
            devices = await client.GetDevicesAsync(ct, useCache: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read UniFi devices for site {Site}", siteSlug);
            return Array.Empty<(string, string)>();
        }

        var found = new List<(string Mac, string Name)>();
        foreach (var device in devices.Where(d => d.DeviceType == DeviceType.CableModem && d.Adopted))
        {
            var mac = NormalizeMac(device.Mac);
            if (mac == null) continue;
            if (UciInformDecoder.ParseKey(device.GetInformKey()) is { } key)
                _keys[(siteSlug, mac)] = key;
            found.Add((mac, string.IsNullOrWhiteSpace(device.Name) ? "UniFi Cable Internet" : device.Name.Trim()));
        }
        return found;
    }

    /// <summary>Remembers that the user deleted this UCI's config, so it is never created again automatically.</summary>
    public async Task DismissAutoCreateAsync(string siteSlug, string mac, CancellationToken ct = default)
    {
        var normalized = NormalizeMac(mac);
        if (normalized == null) return;
        try
        {
            await using var db = _siteDbFactory.CreateForSite(siteSlug, siteSlug == SiteManagementService.DefaultSiteSlug);
            var setting = await db.SystemSettings.FindAsync(new object[] { SystemSettingKeys.UciAutoCreateDismissed }, ct);
            var macs = ParseMacList(setting?.Value);
            if (!macs.Add(normalized)) return;
            var value = string.Join(',', macs.OrderBy(m => m, StringComparer.Ordinal));
            if (setting == null)
                db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.UciAutoCreateDismissed, Value = value });
            else
            {
                setting.Value = value;
                setting.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to remember the deleted UCI {Mac} for site {Site}", normalized, siteSlug);
        }
    }

    private async Task<HashSet<string>?> LoadDismissedAsync(string siteSlug, CancellationToken ct)
    {
        try
        {
            await using var db = _siteDbFactory.CreateForSite(siteSlug, siteSlug == SiteManagementService.DefaultSiteSlug);
            var setting = await db.SystemSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.UciAutoCreateDismissed, ct);
            return ParseMacList(setting?.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable means "unknown", and creating a config the user deleted is worse than
            // waiting one more discovery pass, so null skips this pass.
            _logger.LogDebug(ex, "Failed to read deleted UCIs for site {Site}", siteSlug);
            return null;
        }
    }

    private static HashSet<string> ParseMacList(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeMac).Where(m => m != null).Select(m => m!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Forgets a site's cached state (site removed or its configs reset).</summary>
    public void ForgetSite(string siteSlug)
    {
        _captureTargets.TryRemove(siteSlug, out _);
        _lastKeyRefresh.TryRemove(siteSlug, out _);
        foreach (var key in _snapshots.Keys.Where(k => k.Site == siteSlug).ToList()) _snapshots.TryRemove(key, out _);
        foreach (var key in _keys.Keys.Where(k => k.Site == siteSlug).ToList()) _keys.TryRemove(key, out _);
        foreach (var key in _lastFailure.Keys.Where(k => k.Site == siteSlug).ToList()) _lastFailure.TryRemove(key, out _);
    }

    private void RecordFailure(string site, string mac, string reason)
    {
        var previous = _lastFailure.TryGetValue((site, mac), out var last) ? last.Reason : null;
        _lastFailure[(site, mac)] = (reason, DateTime.UtcNow);
        if (previous != reason)
            _logger.LogWarning("UCI {Mac} (site {Site}): {Reason}", mac, site, reason);
    }

    private async Task<HashSet<string>> GetCaptureTargetsAsync(string site, CancellationToken ct) =>
        _captureTargets.TryGetValue(site, out var cached) ? cached : await LoadCaptureTargetsAsync(site, ct);

    private async Task<HashSet<string>> LoadCaptureTargetsAsync(string site, CancellationToken ct)
    {
        var isDefault = site == SiteManagementService.DefaultSiteSlug;
        await using var db = _siteDbFactory.CreateForSite(site, isDefault);
        var hosts = await db.CmConfigurations.AsNoTracking()
            .Where(c => c.Enabled && c.Provider == ProviderKey)
            .Select(c => c.Host)
            .ToListAsync(ct);
        var targets = hosts.Select(NormalizeMac).Where(m => m != null).Select(m => m!).ToHashSet(StringComparer.Ordinal);
        _captureTargets[site] = targets;
        return targets;
    }

    private async Task<byte[]?> GetKeyAsync(string site, string mac, bool force, CancellationToken ct)
    {
        if (!force && _keys.TryGetValue((site, mac), out var cached))
            return cached;

        var now = DateTime.UtcNow;
        if (_lastKeyRefresh.TryGetValue(site, out var last) && now - last < KeyRefreshMinInterval)
            return _keys.TryGetValue((site, mac), out var existing) ? existing : null;
        _lastKeyRefresh[site] = now;

        try
        {
            var client = _connections.GetFor(site).Client;
            if (client == null)
                return _keys.TryGetValue((site, mac), out var stale) ? stale : null;

            var devices = await client.GetDevicesAsync(ct, useCache: !force);
            foreach (var device in devices.Where(d => d.DeviceType == DeviceType.CableModem))
            {
                var deviceMac = NormalizeMac(device.Mac);
                var deviceKey = UciInformDecoder.ParseKey(device.GetInformKey());
                if (deviceMac != null && deviceKey != null)
                    _keys[(site, deviceMac)] = deviceKey;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch UCI inform keys for site {Site}", site);
        }
        return _keys.TryGetValue((site, mac), out var key) ? key : null;
    }

    /// <summary>
    /// Masks identifiers in a decoded payload before it is logged: MAC addresses, IP addresses, the
    /// serial number, and any x_ field. The rest (field names and DOCSIS values) is what a support
    /// thread needs.
    /// </summary>
    public static string Redact(string json)
    {
        var redacted = MacRx().Replace(json, "xx:xx:xx:xx:xx:xx");
        redacted = Ipv4Rx().Replace(redacted, "x.x.x.x");
        redacted = Ipv6Rx().Replace(redacted, "x:x::x");
        redacted = SerialRx().Replace(redacted, "\"serial\":\"redacted\"");
        redacted = XFieldRx().Replace(redacted, m => $"\"{m.Groups[1].Value}\":\"redacted\"");
        return redacted;
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}\b")]
    private static partial Regex MacRx();

    [GeneratedRegex(@"\b(\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Rx();

    // Requires "::" or five groups, so an event log time such as "14:03:11" is left alone.
    [GeneratedRegex(@"\b(?=[0-9a-fA-F:]*::|(?:[0-9a-fA-F]{1,4}:){5})[0-9a-fA-F]{1,4}(:[0-9a-fA-F]{0,4}){2,7}(%\w+)?")]
    private static partial Regex Ipv6Rx();

    [GeneratedRegex(@"""serial""\s*:\s*""[^""]*""")]
    private static partial Regex SerialRx();

    // UniFi's x_-prefixed fields are device secrets and fingerprints; none belongs in a log.
    [GeneratedRegex(@"""(x_[A-Za-z0-9_]+)""\s*:\s*""[^""]*""")]
    private static partial Regex XFieldRx();
}
