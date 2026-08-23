using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Where a GL.iNet router's modem lives and how <c>gl_modem</c> must be addressed to
/// reach it, plus the identity its firmware reports.
/// </summary>
/// <param name="Bus">Value for gl_modem's <c>-B</c> flag. "cpu" for a modem integrated
/// into the SoC, a USB path such as "1-1.2" for a plug-in module.</param>
/// <param name="Sub">Value for gl_modem's <c>-U</c> flag, or null for firmware whose
/// gl_modem predates the flag.</param>
public sealed record GlModemEndpoint(
    string? Bus,
    int? Sub,
    string? Model = null,
    string? Vendor = null,
    string? Firmware = null)
{
    /// <summary>Endpoint used when discovery finds nothing: let gl_modem pick the modem itself.</summary>
    public static readonly GlModemEndpoint Unknown = new(null, null);

    /// <summary>Human-readable identity for Test Connection, e.g. "Quectel RG650V-NA".</summary>
    public string? Description =>
        string.IsNullOrWhiteSpace(Model) ? null
        : string.IsNullOrWhiteSpace(Vendor) ? Model
        : $"{Capitalize(Vendor!)} {Model}";

    private static string Capitalize(string s) =>
        s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
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
    private readonly ConcurrentDictionary<string, GlModemEndpoint> _endpoints = new();

    private const string UsageMarker = "Usage: gl_modem";

    public GlModemTransport(ILogger<GlModemTransport> logger, SshClientService sshClient)
    {
        _logger = logger;
        _sshClient = sshClient;
    }

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

        var wasCached = _endpoints.TryGetValue(context.CacheKey, out var cached);
        var endpoint = wasCached
            ? cached!
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

        if (result.Success)
            _endpoints[context.CacheKey] = endpoint;

        return result with { Endpoint = endpoint };
    }

    private async Task<GlModemAtResult> ExecuteAsync(
        GlModemEndpoint endpoint,
        SshConnectionInfo connection,
        IReadOnlyList<string> atCommands,
        CancellationToken cancellationToken)
    {
        var script = new StringBuilder();
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

        if (!result.Success)
            return new GlModemAtResult { Success = false, Error = SshFailureSummary.Describe(combined, connection.Host) };

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
        {
            if (!Regex.IsMatch(endpoint.Bus, @"^[0-9A-Za-z._:-]+$"))
                throw new ArgumentException($"Invalid modem bus: {endpoint.Bus}");
            sb.Append($" -B {endpoint.Bus}");
        }

        if (endpoint.Sub.HasValue)
            sb.Append($" -U {endpoint.Sub.Value}");

        sb.Append($" AT '{atCommand}'");
        return sb.ToString();
    }

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
            "echo '===USB==='; ls /sys/bus/usb/devices 2>/dev/null";

        try
        {
            var result = await _sshClient.ExecuteCommandAsync(
                connection, command, cancellationToken: cancellationToken);

            if (result.Success)
            {
                var endpoint = ParseDiscovery(result.Output ?? "", context.TransportPath);
                _logger.LogInformation(
                    "Resolved GL.iNet modem {Name} to bus {Bus} sub {Sub} ({Model})",
                    context.Name, endpoint.Bus ?? "auto", endpoint.Sub?.ToString() ?? "none",
                    endpoint.Description ?? "unidentified");
                return endpoint;
            }

            _logger.LogDebug("Modem discovery command failed on {Name}", context.Name);
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
        var sections = SplitNamedSections(output, new[] { "INFO", "STATUS", "USB" });

        sections.TryGetValue("INFO", out var info);
        sections.TryGetValue("STATUS", out var status);
        sections.TryGetValue("USB", out var usb);

        string? bus = null, model = null, vendor = null, firmware = null;
        int? sub = null;

        if (TryParseJson(info, out var infoDoc))
        {
            using (infoDoc)
            {
                var root = infoDoc.RootElement;
                bus = GetString(root, "bus");
                model = GetString(root, "name");
                vendor = GetString(root, "vendor");
                firmware = GetString(root, "version");
            }
        }

        // The AT subscription follows the SIM slot the modem is actually using.
        if (bus != null && TryParseJson(status, out var statusDoc))
        {
            using (statusDoc)
            {
                if (int.TryParse(GetString(statusDoc.RootElement, "current_sim_slot"), out var slot))
                    sub = slot;
            }
        }

        if (bus != null)
            return new GlModemEndpoint(bus, sub ?? 1, model, vendor, firmware);

        if (!string.IsNullOrWhiteSpace(configuredBus))
            return new GlModemEndpoint(configuredBus, null);

        var usbBus = (usb ?? "")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(d => Regex.IsMatch(d, @"^\d+-[\d.]+$"));

        return usbBus != null ? new GlModemEndpoint(usbBus, null) : GlModemEndpoint.Unknown;
    }

    private static GlModemEndpoint FallbackEndpoint(string? configuredBus) =>
        string.IsNullOrWhiteSpace(configuredBus)
            ? GlModemEndpoint.Unknown
            : new GlModemEndpoint(configuredBus, null);

    private static string SectionMarker(int index) => $"===AT{index}===";

    private static Dictionary<string, string> SplitAtSections(string output, IReadOnlyList<string> atCommands)
    {
        var markers = atCommands.Select((cmd, i) => (Key: cmd, Marker: SectionMarker(i)));
        return SplitSections(output, markers);
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

    public GlModemEndpoint Endpoint { get; init; } = GlModemEndpoint.Unknown;

    public string For(string atCommand) => Sections.TryGetValue(atCommand, out var s) ? s : "";
}
