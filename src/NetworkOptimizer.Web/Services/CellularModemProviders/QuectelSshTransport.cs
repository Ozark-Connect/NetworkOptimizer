using System.Collections.Concurrent;
using System.Text.Json;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// The AT port a generic Quectel router answered on, plus the identity read at discovery.
/// </summary>
/// <param name="Device">AT port device node, e.g. <c>/dev/mhi_DUN</c>.</param>
/// <param name="Vendor">Module manufacturer from <c>ATI</c>.</param>
/// <param name="Model">Module model from <c>ATI</c>.</param>
/// <param name="Revision">Module build from <c>ATI</c>, the fallback when <c>AT+CGMR</c> says nothing.</param>
/// <param name="HostModel">The router's board model, when it names a product rather than a SoC.</param>
/// <param name="HostVersion">The router's OS build.</param>
public sealed record QuectelSshEndpoint(
    string Device,
    string? Vendor = null,
    string? Model = null,
    string? Revision = null,
    string? HostModel = null,
    string? HostVersion = null)
{
    /// <summary>Human-readable identity for Test Connection, e.g. "Quectel RM551E-GL".</summary>
    public string? Description =>
        string.IsNullOrWhiteSpace(Model) ? null
        : string.IsNullOrWhiteSpace(Vendor) ? Model
        : $"{Vendor} {Model}";
}

/// <summary>What one discovery session found.</summary>
/// <param name="Endpoint">The chosen AT port, or null when none qualified.</param>
/// <param name="PresentPorts">Candidate nodes that exist on the router, in probe order.</param>
public sealed record QuectelSshDiscovery(QuectelSshEndpoint? Endpoint, IReadOnlyList<string> PresentPorts);

/// <summary>
/// Runs AT commands on any OpenWrt-style router with a Quectel module, by writing to the module's
/// AT port over SSH. Needs no vendor tooling: no GL.iNet ubus service, no <c>gl_modem</c>, and no
/// <c>timeout</c> applet (see <see cref="AtDeviceShell.TimeoutPrelude"/>).
///
/// The port is found once per modem and cached. A vendor daemon may hold the same port; it is
/// shared, never stopped, because a session that dies between a stop and a resume leaves the
/// router's own connection manager frozen.
/// </summary>
public sealed class QuectelSshTransport
{
    /// <summary>
    /// AT port candidates in probe order. PCIe/MHI first, as on the CP520 in #1269 and GL's
    /// X3000/XE3000. Then the mainline WWAN AT ports, then Quectel's USB AT interfaces (ttyUSB0
    /// is DM and ttyUSB1 is NMEA, so neither is ever written), then SoC-integrated SMD channels,
    /// smd9 first because GL's E5800 reports it as its AT port.
    /// </summary>
    public static readonly IReadOnlyList<string> CandidatePorts = new[]
    {
        "/dev/mhi_DUN",
        "/dev/wwan0at0",
        "/dev/wwan0at1",
        "/dev/ttyUSB2",
        "/dev/ttyUSB3",
        "/dev/smd9",
        "/dev/smd8",
        "/dev/smd7",
        "/dev/smd11",
    };

    private const string IdentityCommand = "ATI";

    // Worst case is every candidate present and silent: one read timeout each, plus SSH setup.
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<QuectelSshTransport> _logger;
    private readonly SshClientService _sshClient;
    // Keyed on CacheKey, holding the configured port the entry was resolved under so editing
    // AT Port in Settings takes effect on the next poll.
    private readonly ConcurrentDictionary<string, (QuectelSshEndpoint Endpoint, string ConfiguredPort)> _endpoints = new();

    public QuectelSshTransport(ILogger<QuectelSshTransport> logger, SshClientService sshClient)
    {
        _logger = logger;
        _sshClient = sshClient;
    }

    /// <summary>
    /// Drop the cached port and identity, so the next poll rediscovers them. Called whenever a poll
    /// fails: a module that rebooted or was upgraded must not keep its old identity.
    /// </summary>
    public void Forget(string cacheKey) => _endpoints.TryRemove(cacheKey, out _);

    /// <summary>
    /// Run one or more AT commands in a single SSH session, discovering the AT port first when it
    /// is not cached. Results come back keyed by the command that produced them.
    /// </summary>
    public async Task<QuectelSshAtResult> RunAtAsync(
        ModemPollContext context,
        SshConnectionInfo connection,
        IReadOnlyList<string> atCommands,
        CancellationToken cancellationToken = default)
    {
        foreach (var at in atCommands)
        {
            if (at.Contains('\'') || at.Contains('\n') || at.Contains('\r'))
                throw new ArgumentException($"Unsupported characters in AT command: {at}");
        }

        var configuredPort = (context.TransportPath ?? "").Trim();
        if (configuredPort.Length > 0 && !AtDeviceShell.IsValidDevice(configuredPort))
            return QuectelSshAtResult.Failed($"AT Port \"{configuredPort}\" is not a device path such as /dev/ttyUSB2.");

        QuectelSshEndpoint endpoint;
        if (_endpoints.TryGetValue(context.CacheKey, out var cached) && cached.ConfiguredPort == configuredPort)
        {
            endpoint = cached.Endpoint;
        }
        else
        {
            var (discovery, error) = await DiscoverAsync(context, connection, configuredPort, cancellationToken);
            if (discovery?.Endpoint == null)
                return QuectelSshAtResult.Failed(error ?? NoPortMessage(discovery?.PresentPorts ?? Array.Empty<string>(), configuredPort, context.Host));
            endpoint = discovery.Endpoint;
        }

        var result = await ExecuteAsync(endpoint, connection, atCommands, cancellationToken);

        // Cache only what the modem answered on: the markers print whether or not it did.
        if (result.Success && result.Sections.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
            _endpoints[context.CacheKey] = (endpoint, configuredPort);
        else
            _endpoints.TryRemove(context.CacheKey, out _);

        return result with { Endpoint = endpoint };
    }

    private async Task<QuectelSshAtResult> ExecuteAsync(
        QuectelSshEndpoint endpoint,
        SshConnectionInfo connection,
        IReadOnlyList<string> atCommands,
        CancellationToken cancellationToken)
    {
        var result = await _sshClient.ExecuteCommandAsync(
            connection, BuildBatchScript(endpoint.Device, atCommands), cancellationToken: cancellationToken);

        // A chain's exit status is only its last command's. Transport failures produce no stdout.
        if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
            return QuectelSshAtResult.Failed(SshFailureSummary.Describe(result.CombinedOutput ?? "", connection.Host));

        var markers = atCommands.Select((cmd, i) => (Key: cmd, Marker: SectionMarker(i)));
        return new QuectelSshAtResult
        {
            Success = true,
            Sections = AtDeviceShell.SplitSections(result.Output ?? "", markers),
        };
    }

    private async Task<(QuectelSshDiscovery? Discovery, string? Error)> DiscoverAsync(
        ModemPollContext context,
        SshConnectionInfo connection,
        string configuredPort,
        CancellationToken cancellationToken)
    {
        var result = await _sshClient.ExecuteCommandAsync(
            connection, BuildDiscoveryScript(configuredPort), DiscoveryTimeout, cancellationToken);

        // Judged on output, not exit status: the script exits on the first port that answers,
        // and any probe before it can fail without invalidating the rest.
        if (string.IsNullOrWhiteSpace(result.Output))
        {
            _logger.LogDebug("Quectel port discovery returned nothing on {Name}", context.Name);
            return (null, SshFailureSummary.Describe(result.CombinedOutput ?? "", connection.Host));
        }

        var discovery = ParseDiscovery(result.Output, configuredPort);
        if (discovery.Endpoint is { } endpoint)
        {
            _logger.LogInformation(
                "Resolved Quectel modem {Name} on {Device} ({Model} {Revision}, host {HostModel} {HostVersion})",
                context.Name, endpoint.Device, endpoint.Description ?? "unidentified",
                endpoint.Revision ?? "unknown", endpoint.HostModel ?? "unknown", endpoint.HostVersion ?? "unknown");
        }
        else
        {
            _logger.LogWarning(
                "No Quectel AT port answered on {Name}; present: {Ports}",
                context.Name, discovery.PresentPorts.Count > 0 ? string.Join(", ", discovery.PresentPorts) : "none");
        }

        return (discovery, null);
    }

    /// <summary>
    /// The discovery script: board and release, then <c>ATI</c> on each present candidate in
    /// order, stopping at the first Quectel answer. Only the configured port is probed when one is
    /// set. Internal for tests.
    /// </summary>
    internal static string BuildDiscoveryScript(string? configuredPort)
    {
        var parts = new List<string>
        {
            AtDeviceShell.TimeoutPrelude,
            "echo '===BOARD==='; ubus call system board 2>/dev/null",
            "echo '===RELEASE==='; cat /etc/openwrt_release 2>/dev/null",
        };

        foreach (var device in PortsToProbe(configuredPort))
        {
            parts.Add(
                $"if [ -c {device} ]; then echo '{PortMarker(device)}'; " +
                $"r=$({BuildCommand(device, IdentityCommand)}); echo \"$r\"; " +
                "case \"$r\" in *Quectel*OK*) exit 0;; esac; fi");
        }

        return string.Join("; ", parts);
    }

    /// <summary>Parse the discovery script's output. Internal for tests.</summary>
    internal static QuectelSshDiscovery ParseDiscovery(string output, string? configuredPort)
    {
        var ports = PortsToProbe(configuredPort);
        var markers = new List<(string Key, string Marker)>
        {
            ("BOARD", "===BOARD==="),
            ("RELEASE", "===RELEASE==="),
        };
        markers.AddRange(ports.Select(d => (Key: d, Marker: PortMarker(d))));

        var sections = AtDeviceShell.SplitSections(output, markers);
        var present = ports.Where(sections.ContainsKey).ToList();

        sections.TryGetValue("BOARD", out var board);
        sections.TryGetValue("RELEASE", out var release);
        var (hostModel, boardVersion) = ParseBoard(board);
        var hostVersion = boardVersion ?? ParseReleaseDescription(release);

        // A configured port is the owner's call: used even when ATI did not identify the module.
        var chosen = string.IsNullOrEmpty(configuredPort)
            ? present.FirstOrDefault(d => QuectelAtParser.IsQuectelIdentityResponse(sections[d]))
            : present.FirstOrDefault();

        if (chosen == null)
            return new QuectelSshDiscovery(null, present);

        var identity = QuectelAtParser.IsQuectelIdentityResponse(sections[chosen])
            ? QuectelAtParser.ParseIdentity(sections[chosen])
            : null;

        return new QuectelSshDiscovery(
            new QuectelSshEndpoint(chosen, identity?.Vendor, identity?.Model, identity?.Revision, hostModel, hostVersion),
            present);
    }

    /// <summary>
    /// One AT command against a device. <c>/dev/mhi_*</c> keeps the two-open form proven on GL's
    /// MHI routers; every other node is opened once, because WWAN AT ports refuse a second opener
    /// and a tty can drop the answer between two opens. Internal for tests.
    /// </summary>
    internal static string BuildCommand(string device, string atCommand) =>
        device.StartsWith("/dev/mhi_", StringComparison.Ordinal)
            ? AtDeviceShell.BuildTwoOpenCommand(device, atCommand, "_to")
            : AtDeviceShell.BuildSingleOpenCommand(device, atCommand);

    /// <summary>The poll script: each command's answer under its own marker. Internal for tests.</summary>
    internal static string BuildBatchScript(string device, IReadOnlyList<string> atCommands)
    {
        var parts = new List<string> { AtDeviceShell.TimeoutPrelude };
        for (int i = 0; i < atCommands.Count; i++)
            parts.Add($"echo '{SectionMarker(i)}'; {BuildCommand(device, atCommands[i])}");
        return string.Join("; ", parts);
    }

    /// <summary>The Test Connection and poll error when discovery found no usable port. Internal for tests.</summary>
    internal static string NoPortMessage(IReadOnlyList<string> presentPorts, string configuredPort, string host)
    {
        if (configuredPort.Length > 0)
            return $"AT Port {configuredPort} does not exist on {host}.";

        if (presentPorts.Count == 0)
            return $"No modem AT port was found on {host}. Checked {string.Join(", ", CandidatePorts)}. " +
                   "Set AT Port if the module uses another device node.";

        return $"No Quectel module answered ATI on {string.Join(", ", presentPorts)}. " +
               "Set AT Port if the module uses another device node.";
    }

    private static IReadOnlyList<string> PortsToProbe(string? configuredPort) =>
        string.IsNullOrEmpty(configuredPort) ? CandidatePorts : new[] { configuredPort };

    private static string PortMarker(string device) => $"===PORT:{device}===";

    private static string SectionMarker(int index) => $"===AT{index}===";

    /// <summary>
    /// The board model up to its first comma, and the OS build. A model that starts with the SoC
    /// maker ("Qualcomm Technologies, Inc. ...") names a reference design, not the product, so it
    /// is dropped.
    /// </summary>
    private static (string? Model, string? Version) ParseBoard(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);

            string? model = GetString(root, "model");
            if (model != null)
            {
                var comma = model.IndexOf(',');
                model = (comma > 0 ? model[..comma] : model).Trim();
                if (model.Length == 0 || model.StartsWith("Qualcomm Technologies", StringComparison.OrdinalIgnoreCase))
                    model = null;
            }

            string? version = null;
            if (root.TryGetProperty("release", out var release))
                version = GetString(release, "description") ?? GetString(release, "version");

            return (model, string.IsNullOrWhiteSpace(version) ? null : version.Trim());
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary><c>DISTRIB_DESCRIPTION</c> from /etc/openwrt_release, for routers whose ubus has no board call.</summary>
    private static string? ParseReleaseDescription(string? release)
    {
        if (string.IsNullOrWhiteSpace(release))
            return null;

        foreach (var line in release.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("DISTRIB_DESCRIPTION=", StringComparison.Ordinal))
                continue;

            var value = line["DISTRIB_DESCRIPTION=".Length..].Trim().Trim('\'', '"').Trim();
            return value.Length > 0 ? value : null;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Outcome of a batch of AT commands run through <see cref="QuectelSshTransport"/>.</summary>
public sealed record QuectelSshAtResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>Output of each AT command, keyed by the command itself.</summary>
    public Dictionary<string, string> Sections { get; init; } = new();

    /// <summary>The AT port and identity the batch ran against, or null when none was found.</summary>
    public QuectelSshEndpoint? Endpoint { get; init; }

    public string For(string atCommand) => Sections.TryGetValue(atCommand, out var s) ? s : "";

    internal static QuectelSshAtResult Failed(string error) => new() { Success = false, Error = error };
}
