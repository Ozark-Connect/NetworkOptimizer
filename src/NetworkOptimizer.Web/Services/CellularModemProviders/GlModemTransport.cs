using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Where a GL.iNet router's modem lives and how to reach it, plus the identity its
/// firmware reports. Two transports: <c>gl_modem</c> (USB, addressed by <see cref="Bus"/>
/// and <see cref="Sub"/>) and direct MHI (PCIe, addressed by <see cref="MhiDevice"/>).
/// </summary>
/// <param name="Bus">Value for gl_modem's <c>-B</c> flag. "cpu" for a modem integrated
/// into the SoC, a USB path such as "1-1.2" for a plug-in module.</param>
/// <param name="Sub">Value for gl_modem's <c>-U</c> flag, or null for firmware whose
/// gl_modem predates the flag.</param>
/// <param name="MhiDevice">Device node for a PCIe/MHI modem (e.g. "/dev/mhi_DUN").
/// When set, AT commands are written directly to this device instead of through
/// gl_modem. The X3000/XE3000 with an RM520N-GL use this path.</param>
public sealed record GlModemEndpoint(
    string? Bus,
    int? Sub,
    string? Model = null,
    string? Vendor = null,
    string? SoftwareVersion = null,
    string? HostVersion = null,
    string? Product = null,
    string? MhiDevice = null)
{
    /// <summary>Endpoint used when discovery finds nothing: let gl_modem pick the modem itself.</summary>
    public static readonly GlModemEndpoint Unknown = new(null, null);

    /// <summary>Whether AT commands go through a PCIe/MHI device node rather than gl_modem.</summary>
    public bool IsMhi => !string.IsNullOrEmpty(MhiDevice);

    /// <summary>Human-readable identity for Test Connection, e.g. "Quectel RG650V-NA".</summary>
    public string? Description =>
        string.IsNullOrWhiteSpace(Model) ? null
        : string.IsNullOrWhiteSpace(Vendor) ? Model
        : $"{Vendor} {Model}";
}

/// <summary>
/// Runs AT commands on a GL.iNet router over SSH, resolving the gl_modem addressing
/// once per modem and caching it.
///
/// Addressing is not guessable from the outside. A 5G unit like the E5800 carries the
/// modem on the SoC, where the bus is the literal string "cpu" and <c>-U</c> is
/// mandatory; a plug-in module sits on a USB path, and older firmware has no <c>-U</c>
/// at all. Getting it wrong is silent: gl_modem prints its usage text and exits, which
/// reads as "the modem said nothing" rather than as a bad command line.
/// </summary>
public sealed class GlModemTransport
{
    private readonly ILogger<GlModemTransport> _logger;
    private readonly SshClientService _sshClient;
    // Keyed on CacheKey, holding the configured bus the entry was resolved under so editing
    // Modem Bus in Settings takes effect on the next poll instead of waiting for a restart.
    private readonly ConcurrentDictionary<string, (GlModemEndpoint Endpoint, string ConfiguredBus)> _endpoints = new();

    private const string UsageMarker = "Usage: gl_modem";

    public GlModemTransport(ILogger<GlModemTransport> logger, SshClientService sshClient)
    {
        _logger = logger;
        _sshClient = sshClient;
    }

    /// <summary>
    /// Drop what we know about this modem, so the next poll rediscovers it. Called whenever a
    /// poll fails: a firmware upgrade or rollback reboots the router, and the cached endpoint
    /// carries its versions, which must not outlive the firmware they name.
    /// </summary>
    public void Forget(string cacheKey) => _endpoints.TryRemove(cacheKey, out _);

    /// <summary>
    /// Run one or more AT commands in a single SSH session. Results come back keyed by
    /// the command that produced them.
    /// </summary>
    public async Task<GlModemAtResult> RunAtAsync(
        ModemPollContext context,
        SshConnectionInfo connection,
        IReadOnlyList<string> atCommands,
        CancellationToken cancellationToken = default)
    {
        foreach (var at in atCommands)
        {
            if (at.Contains('\'') || at.Contains('\n'))
                throw new ArgumentException($"Unsupported characters in AT command: {at}");
        }

        var wasCached = _endpoints.TryGetValue(context.CacheKey, out var cached)
                        && cached.ConfiguredBus == context.TransportPath;
        var endpoint = wasCached
            ? cached.Endpoint
            : await DiscoverAsync(context, connection, cancellationToken);

        var result = await ExecuteAsync(endpoint, connection, atCommands, cancellationToken);

        // gl_modem answering with its usage text means the addressing is stale, not that
        // the modem is silent. Rediscover once and retry before reporting a failure.
        if (result.RejectedCommandLine && wasCached)
        {
            _logger.LogInformation(
                "gl_modem rejected the command line on {Name}; rediscovering the modem endpoint", context.Name);
            _endpoints.TryRemove(context.CacheKey, out _);
            endpoint = await DiscoverAsync(context, connection, cancellationToken);
            result = await ExecuteAsync(endpoint, connection, atCommands, cancellationToken);
        }

        // Cache only what the modem actually answered on. The section markers echo whether or
        // not gl_modem produced anything, so Success alone would pin a wrong bus in place and
        // every later poll would report no data.
        if (result.Success && result.Sections.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
            _endpoints[context.CacheKey] = (endpoint, context.TransportPath);
        else
            _endpoints.TryRemove(context.CacheKey, out _);

        return result with { Endpoint = endpoint };
    }

    private Task<GlModemAtResult> ExecuteAsync(
        GlModemEndpoint endpoint,
        SshConnectionInfo connection,
        IReadOnlyList<string> atCommands,
        CancellationToken cancellationToken) =>
        endpoint.IsMhi
            ? ExecuteMhiAsync(endpoint, connection, atCommands, cancellationToken)
            : ExecuteGlModemAsync(endpoint, connection, atCommands, cancellationToken);

    private async Task<GlModemAtResult> ExecuteGlModemAsync(
        GlModemEndpoint endpoint,
        SshConnectionInfo connection,
        IReadOnlyList<string> atCommands,
        CancellationToken cancellationToken)
    {
        var script = new StringBuilder();

        // Module firmware is read in the same session as the readings it labels, never from the
        // cached endpoint: an upgrade would otherwise leave the old version standing against new
        // data. GL's ubus carries the fuller string, so it is asked first and AT+CGMR backs it up.
        if (!string.IsNullOrWhiteSpace(endpoint.Bus))
        {
            script.Append($"echo '{FirmwareMarker}'; ubus call cellular.modem info ");
            script.Append($"'{{\"bus\":\"{ValidBus(endpoint.Bus)}\"}}' 2>/dev/null; ");
        }

        for (int i = 0; i < atCommands.Count; i++)
        {
            if (i > 0) script.Append("; ");
            script.Append($"echo '{SectionMarker(i)}'; ");
            script.Append(BuildAtCommand(endpoint, atCommands[i]));
        }

        var result = await _sshClient.ExecuteCommandAsync(
            connection, script.ToString(), cancellationToken: cancellationToken);

        var combined = result.CombinedOutput ?? "";
        if (combined.Contains(UsageMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new GlModemAtResult
            {
                Success = false,
                RejectedCommandLine = true,
                Error = "gl_modem did not accept the command line for this modem.",
            };
        }

        // A chain's exit status is only the last command's, so it cannot speak for the rest:
        // judging the batch by it would let a failed enrichment command discard signal data
        // already in hand. Transport failures still surface, having produced no stdout at all.
        if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
            return new GlModemAtResult { Success = false, Error = SshFailureSummary.Describe(combined, connection.Host) };

        var sections = SplitAtSections(result.Output ?? "", atCommands);
        sections.Remove(FirmwareSectionKey, out var firmwareJson);

        return new GlModemAtResult
        {
            Success = true,
            Sections = sections,
            ModuleFirmware = ParseModuleFirmware(firmwareJson),
        };
    }

    /// <summary>
    /// Execute AT commands by writing directly to a PCIe/MHI device node. Used on
    /// routers like the X3000/XE3000 whose RM520N-GL sits on PCIe and is unreachable
    /// by gl_modem.
    /// </summary>
    private async Task<GlModemAtResult> ExecuteMhiAsync(
        GlModemEndpoint endpoint,
        SshConnectionInfo connection,
        IReadOnlyList<string> atCommands,
        CancellationToken cancellationToken)
    {
        var device = ValidMhiDevice(endpoint.MhiDevice!);
        var script = new StringBuilder();

        for (int i = 0; i < atCommands.Count; i++)
        {
            if (i > 0) script.Append("; ");
            script.Append($"echo '{SectionMarker(i)}'; ");
            script.Append(BuildMhiCommand(device, atCommands[i]));
        }

        var result = await _sshClient.ExecuteCommandAsync(
            connection, script.ToString(), cancellationToken: cancellationToken);

        if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
            return new GlModemAtResult { Success = false, Error = SshFailureSummary.Describe(result.CombinedOutput ?? "", connection.Host) };

        return new GlModemAtResult
        {
            Success = true,
            Sections = SplitAtSections(result.Output ?? "", atCommands),
        };
    }

    /// <summary>
    /// Build the gl_modem invocation for one AT command. Both flags are omitted when
    /// unknown, which is the form older firmware accepts.
    /// </summary>
    internal static string BuildAtCommand(GlModemEndpoint endpoint, string atCommand)
    {
        var sb = new StringBuilder("gl_modem");

        if (!string.IsNullOrWhiteSpace(endpoint.Bus))
            sb.Append($" -B {ValidBus(endpoint.Bus)}");

        if (endpoint.Sub.HasValue)
            sb.Append($" -U {endpoint.Sub.Value}");

        sb.Append($" AT '{atCommand}'");
        return sb.ToString();
    }

    /// <summary>
    /// Build a direct AT command to a PCIe/MHI device. Writes the command, then reads
    /// until the modem answers OK or ERROR (with a timeout fallback).
    /// </summary>
    internal static string BuildMhiCommand(string device, string atCommand)
    {
        device = ValidMhiDevice(device);
        return $"echo -ne '{atCommand}\\r' > {device}; " +
               $"timeout 3 sh -c 'while IFS= read -r l; do echo \"$l\"; " +
               $"case \"$l\" in OK*|ERROR*|+CME\\ ERROR*|+CMS\\ ERROR*) break;; esac; done < {device}'";
    }

    /// <summary>A bus is interpolated into a shell command, so it may only look like a path.</summary>
    private static string ValidBus(string bus) =>
        Regex.IsMatch(bus, @"^[0-9A-Za-z._:-]+$")
            ? bus
            : throw new ArgumentException($"Invalid modem bus: {bus}");

    private static string ValidMhiDevice(string device) =>
        Regex.IsMatch(device, @"^/dev/[A-Za-z0-9_]+$")
            ? device
            : throw new ArgumentException($"Invalid MHI device: {device}");

    /// <summary>
    /// Find the modem's addressing. GL.iNet's own cellular ubus service answers for the
    /// integrated modems and carries the identity with it; USB enumeration covers the
    /// plug-in modules, where the configured bus wins if the user set one.
    /// </summary>
    public async Task<GlModemEndpoint> DiscoverAsync(
        ModemPollContext context,
        SshConnectionInfo connection,
        CancellationToken cancellationToken = default)
    {
        const string command =
            "echo '===INFO==='; ubus call cellular.modem info '{\"bus\":\"cpu\"}' 2>/dev/null; " +
            "echo '===STATUS==='; ubus call cellular.modem status '{\"bus\":\"cpu\"}' 2>/dev/null; " +
            "echo '===GLVER==='; cat /etc/glversion 2>/dev/null; " +
            "echo '===BOARD==='; ubus call system board 2>/dev/null; " +
            "echo '===MHI==='; test -c /dev/mhi_DUN && echo yes || echo no";

        try
        {
            var result = await _sshClient.ExecuteCommandAsync(
                connection, command, cancellationToken: cancellationToken);

            // Judged on output, not exit status: any command in the chain can fail without
            // invalidating what the others returned.
            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                var endpoint = ParseDiscovery(result.Output ?? "", context.TransportPath);
                var transport = endpoint.IsMhi ? $"mhi:{endpoint.MhiDevice}" : $"bus:{endpoint.Bus ?? "auto"} sub:{endpoint.Sub?.ToString() ?? "none"}";
                _logger.LogInformation(
                    "Resolved GL.iNet modem {Name} via {Transport} ({Model} {Software}, host {Host})",
                    context.Name, transport,
                    endpoint.Description ?? "unidentified", endpoint.SoftwareVersion ?? "unknown",
                    endpoint.HostVersion ?? "unknown");
                return endpoint;
            }

            _logger.LogDebug("Modem discovery returned nothing on {Name}", context.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Modem discovery failed on {Name}", context.Name);
        }

        return FallbackEndpoint(context.TransportPath);
    }

    /// <summary>Parse the discovery script's sections. Internal for tests.</summary>
    internal static GlModemEndpoint ParseDiscovery(string output, string? configuredBus)
    {
        var sections = SplitNamedSections(output, new[] { "INFO", "STATUS", "GLVER", "BOARD", "MHI" });

        sections.TryGetValue("INFO", out var info);
        sections.TryGetValue("STATUS", out var status);

        var hostVersion = ParseHostVersion(sections);
        var product = ParseProduct(sections);
        var hasMhi = sections.TryGetValue("MHI", out var mhi) &&
                     mhi.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);

        string? bus = null, model = null, vendor = null, software = null;
        int? sub = null;

        if (TryParseJson(info, out var infoDoc))
        {
            using (infoDoc)
            {
                var root = infoDoc.RootElement;
                bus = GetString(root, "bus");
                model = GetString(root, "name");
                vendor = Capitalize(GetString(root, "vendor"));
                software = GetString(root, "version");
            }
        }

        // Empty strings from ubus mean "I don't know", not "the modem is at bus ''".
        if (string.IsNullOrWhiteSpace(bus)) bus = null;
        if (string.IsNullOrWhiteSpace(model)) model = null;
        if (string.IsNullOrWhiteSpace(vendor)) vendor = null;
        if (string.IsNullOrWhiteSpace(software)) software = null;

        // The AT subscription follows the SIM slot the modem is actually using.
        if (bus != null && TryParseJson(status, out var statusDoc))
        {
            using (statusDoc)
            {
                sub = GetIntFlexible(statusDoc.RootElement, "current_sim_slot");
            }
        }

        if (bus != null)
            return new GlModemEndpoint(bus, sub ?? 1, model, vendor, software, hostVersion, product);

        // PCIe/MHI modem (X3000, XE3000 with RM520N-GL): gl_modem can't reach it, but
        // /dev/mhi_DUN carries AT commands directly.
        if (hasMhi)
            return new GlModemEndpoint(null, null, model, vendor, software, hostVersion, product,
                MhiDevice: "/dev/mhi_DUN");

        if (!string.IsNullOrWhiteSpace(configuredBus))
            return new GlModemEndpoint(configuredBus, null, HostVersion: hostVersion, Product: product);

        // No bus rather than a guess from USB enumeration: `ls` sorts lexically, so a modem
        // behind a hub loses to the hub, and gl_modem auto-detects the plug-in modules this
        // would have been guessing for. Never re-add it: forcing -B is worse than omitting it.
        return GlModemEndpoint.Unknown with { HostVersion = hostVersion, Product = product };
    }

    /// <summary>
    /// The router model the owner bought, from the board's model string
    /// ("GL.iNet E5800, Qualcomm Technologies, Inc. SDXPINN IDP MBB" gives "E5800").
    /// The brand is implied by the provider, so it is stripped rather than stored twice.
    /// </summary>
    private static string? ParseProduct(Dictionary<string, string> sections)
    {
        if (!sections.TryGetValue("BOARD", out var board) || !TryParseJson(board, out var doc))
            return null;

        using (doc)
        {
            var model = GetString(doc.RootElement, "model");
            if (string.IsNullOrWhiteSpace(model))
                return null;

            var comma = model.IndexOf(',');
            if (comma > 0)
                model = model[..comma];

            model = Regex.Replace(model.Trim(), @"^GL[.-]?iNet\s+", "", RegexOptions.IgnoreCase).Trim();
            return model.Length > 0 ? model : null;
        }
    }

    /// <summary>
    /// The router's own build. GL stamps their firmware version in /etc/glversion - the number
    /// their UI shows and the one an owner quotes - so it wins over the OpenWrt base underneath.
    /// </summary>
    private static string? ParseHostVersion(Dictionary<string, string> sections)
    {
        if (sections.TryGetValue("GLVER", out var glVersion))
        {
            var trimmed = glVersion.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        if (sections.TryGetValue("BOARD", out var board) && TryParseJson(board, out var boardDoc))
        {
            using (boardDoc)
            {
                if (boardDoc.RootElement.ValueKind == JsonValueKind.Object &&
                    boardDoc.RootElement.TryGetProperty("release", out var release))
                {
                    return GetString(release, "description") ?? GetString(release, "version");
                }
            }
        }

        return null;
    }

    private static GlModemEndpoint FallbackEndpoint(string? configuredBus) =>
        string.IsNullOrWhiteSpace(configuredBus)
            ? GlModemEndpoint.Unknown
            : new GlModemEndpoint(configuredBus, null);

    private static string SectionMarker(int index) => $"===AT{index}===";

    // Never an AT command, so it cannot collide with a section keyed by one.
    private const string FirmwareSectionKey = "firmware";
    private const string FirmwareMarker = "===VER===";

    private static Dictionary<string, string> SplitAtSections(string output, IReadOnlyList<string> atCommands)
    {
        var markers = atCommands.Select((cmd, i) => (Key: cmd, Marker: SectionMarker(i)))
            .Prepend((Key: FirmwareSectionKey, Marker: FirmwareMarker));
        return SplitSections(output, markers);
    }

    /// <summary>The module's own firmware, from GL's ubus modem info. Internal for tests.</summary>
    internal static string? ParseModuleFirmware(string? json)
    {
        if (!TryParseJson(json, out var doc))
            return null;

        using (doc)
            return GetString(doc.RootElement, "version");
    }

    private static Dictionary<string, string> SplitNamedSections(string output, IReadOnlyList<string> keys)
    {
        var markers = keys.Select(k => (Key: k, Marker: $"==={k}==="));
        return SplitSections(output, markers);
    }

    private static Dictionary<string, string> SplitSections(
        string output, IEnumerable<(string Key, string Marker)> markers)
    {
        var markerList = markers.ToList();
        var sections = new Dictionary<string, string>();
        string? current = null;
        var buffer = new StringBuilder();

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            var match = markerList.FirstOrDefault(m => trimmed == m.Marker);
            if (match.Marker != null)
            {
                if (current != null) sections[current] = buffer.ToString();
                current = match.Key;
                buffer.Clear();
                continue;
            }
            buffer.AppendLine(line);
        }

        if (current != null) sections[current] = buffer.ToString();
        return sections;
    }

    private static bool TryParseJson(string? text, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads a property that firmware may encode as either a JSON string or a number.</summary>
    private static int? GetIntFlexible(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static string? Capitalize(string? s) =>
        string.IsNullOrEmpty(s) || !char.IsLower(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Outcome of a batch of AT commands run through <see cref="GlModemTransport"/>.</summary>
public sealed record GlModemAtResult
{
    public bool Success { get; init; }

    /// <summary>gl_modem answered with its usage text, so no AT command reached the modem.</summary>
    public bool RejectedCommandLine { get; init; }

    public string? Error { get; init; }

    /// <summary>Output of each AT command, keyed by the command itself.</summary>
    public Dictionary<string, string> Sections { get; init; } = new();

    /// <summary>Module firmware read in this same session, so it is never a cached value.</summary>
    public string? ModuleFirmware { get; init; }

    public GlModemEndpoint Endpoint { get; init; } = GlModemEndpoint.Unknown;

    public string For(string atCommand) => Sections.TryGetValue(atCommand, out var s) ? s : "";
}
