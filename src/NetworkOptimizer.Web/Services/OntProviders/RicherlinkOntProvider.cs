using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for the Richerlink RL821GWV-D GPON ONT (Airtel India FTTH), polled in bridge
/// mode. Its China Telecom "e8"-style web UI renders the PON optics into inline script on
/// /cgi-bin/sta-network.asp as raw SFF-8472 DDM words, which this provider converts.
/// <para>
/// Sign-in is not implemented: the only trace was captured after login, so the login request
/// and session mechanism are unknown. A page that comes back as a sign-in page fails the poll
/// with <see cref="SignInPendingMessage"/> rather than guessing at a login form.
/// </para>
/// </summary>
[VendorSpecific("Richerlink", "e8-style /cgi-bin/*.asp pages: PonInfoClass script literals in raw SFF-8472 units")]
public sealed partial class RicherlinkOntProvider : IOntProvider
{
    public string ProviderKey => "richerlink-ont";
    public string DisplayName => "Richerlink RL821GWV-D (HTTP)";

    internal const string DefaultModel = "RL821GWV-D";

    private const int TimeoutSeconds = 10;
    private const string NetworkPath = "/cgi-bin/sta-network.asp";
    private const string DevicePath = "/cgi-bin/sta-device.asp";
    private const string ContentPath = "/cgi-bin/content.asp";

    // Each page counts as data only when it carries the marker its parser reads.
    private const string NetworkMarker = "PonInfoClass";
    private const string DeviceMarker = "GPON SN";
    private const string ContentMarker = "ModelName";

    private readonly ILogger<RicherlinkOntProvider> _logger;

    public RicherlinkOntProvider(ILogger<RicherlinkOntProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>What a fetched page turned out to be.</summary>
    internal enum PageKind
    {
        /// <summary>The page carries the data its parser reads.</summary>
        Data,
        /// <summary>The device answered with a sign-in page instead of the data page.</summary>
        SignIn,
        /// <summary>Neither: some other page or device answered.</summary>
        Unrecognized,
    }

    /// <summary>The failure text for a device that asks for a sign-in this provider cannot do yet.</summary>
    internal static string SignInPendingMessage(string host) =>
        $"{(string.IsNullOrWhiteSpace(host) ? "The device" : host)} requires sign-in. " +
        "Login support for the Richerlink RL821GWV-D is pending.";

    public async Task<PollResult<OntStats>> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Richerlink ONT poll requested but Host is empty (config {Id})", context.Id);
            return PollResult<OntStats>.Failed("No address is configured for this device.");
        }

        try
        {
            using var client = CreateClient(context.Dialer);
            var baseUrl = BuildBaseUrl(context);

            var networkHtml = await GetPageAsync(client, $"{baseUrl}{NetworkPath}", cancellationToken);
            switch (ClassifyPage(networkHtml, NetworkMarker))
            {
                case PageKind.SignIn:
                    _logger.LogWarning("Richerlink ONT {Name} at {Host} returned a sign-in page", context.Name, context.Host);
                    return PollResult<OntStats>.Failed(SignInPendingMessage(context.Host));
                case PageKind.Unrecognized:
                    _logger.LogWarning("Richerlink ONT {Name} at {Host}: {Path} has no PON data", context.Name, context.Host, NetworkPath);
                    return PollResult<OntStats>.Failed(HttpFailureSummary.ForResponse(null, context.Host));
            }

            var stats = new OntStats
            {
                Timestamp = DateTime.UtcNow,
                DeviceHost = context.Host,
                DeviceName = context.Name,
                DeviceModel = DefaultModel,
                VendorName = "Richerlink",
            };
            ApplyNetworkPage(networkHtml, stats);

            // Identity pages are best-effort: the optics above are the poll, these only label it.
            var deviceHtml = await TryGetPageAsync(client, $"{baseUrl}{DevicePath}", context, cancellationToken);
            if (deviceHtml != null && ClassifyPage(deviceHtml, DeviceMarker) == PageKind.Data)
                ApplyDeviceInfo(ParseDevicePage(deviceHtml), stats);

            var contentHtml = await TryGetPageAsync(client, $"{baseUrl}{ContentPath}", context, cancellationToken);
            if (contentHtml != null && ParseModelName(contentHtml) is { } model)
                stats.DeviceModel = model;

            _logger.LogDebug(
                "Richerlink ONT {Name} polled: Rx={Rx} dBm, Tx={Tx} dBm, Link={Link}",
                context.Name, stats.RxPowerDbm?.ToString("F2") ?? "-",
                stats.TxPowerDbm?.ToString("F2") ?? "-", stats.LinkState ?? "-");

            return PollResult<OntStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (IsSignInStatus(ex.StatusCode))
        {
            _logger.LogWarning(ex, "Richerlink ONT {Name} at {Host} refused the request without a sign-in", context.Name, context.Host);
            return PollResult<OntStats>.Failed(SignInPendingMessage(context.Host));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Richerlink ONT {Name} at {Host}", context.Name, context.Host);
            return PollResult<OntStats>.Failed(HttpFailureSummary.Describe(ex, context.Host));
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            using var client = CreateClient(context.Dialer);
            var html = await GetPageAsync(client, $"{BuildBaseUrl(context)}{NetworkPath}", cancellationToken);

            switch (ClassifyPage(html, NetworkMarker))
            {
                case PageKind.SignIn:
                    return (false, SignInPendingMessage(context.Host));
                case PageKind.Unrecognized:
                    return (false, "Connected but response does not contain expected PON status fields");
            }

            var stats = new OntStats();
            ApplyNetworkPage(html, stats);
            return (true,
                $"Connected (HTTP) - RX: {stats.RxPowerDbm?.ToString("F2") ?? "?"} dBm, TX: {stats.TxPowerDbm?.ToString("F2") ?? "?"} dBm");
        }
        catch (HttpRequestException ex) when (IsSignInStatus(ex.StatusCode))
        {
            return (false, SignInPendingMessage(context.Host));
        }
        catch (Exception ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.Host));
        }
    }

    /// <summary>
    /// Decides whether a page is the data page (it carries <paramref name="dataMarker"/>), a
    /// sign-in page, or something else. Every signed-in page links <c>logout.cgi</c>, so a page
    /// without the marker or that link, and with a password field or login reference, is a
    /// sign-in page.
    /// </summary>
    internal static PageKind ClassifyPage(string? html, string dataMarker)
    {
        if (string.IsNullOrEmpty(html))
            return PageKind.Unrecognized;
        if (html.Contains(dataMarker, StringComparison.Ordinal))
            return PageKind.Data;
        if (html.Contains("logout.cgi", StringComparison.OrdinalIgnoreCase))
            return PageKind.Unrecognized;
        return SignInHintRegex().IsMatch(html) ? PageKind.SignIn : PageKind.Unrecognized;
    }

    /// <summary>
    /// Applies sta-network.asp's <c>PonInfoClass</c> values to <paramref name="stats"/>. Missing
    /// or malformed fields stay at their defaults. Returns false when the block is absent.
    /// </summary>
    internal static bool ApplyNetworkPage(string html, OntStats stats)
    {
        var start = html.IndexOf("function PonInfoClass", StringComparison.Ordinal);
        if (start < 0)
            return false;

        var end = html.IndexOf("new PonInfoClass", start, StringComparison.Ordinal);
        var block = end > start ? html[start..end] : html[start..];

        // First assignment wins: later ones sit in if-branches that rewrite the display values.
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in PonFieldRegex().Matches(block))
            fields.TryAdd(m.Groups[1].Value, m.Groups[2].Value);

        var linkSta = Get(fields, "LinkSta");

        // The page shows "-" for every optic when LinkSta is '0'; mirror that.
        if (linkSta != "0")
        {
            stats.RxPowerDbm = RawPowerToDbm(Get(fields, "RxPower"));
            stats.TxPowerDbm = RawPowerToDbm(Get(fields, "TxPower"));
            stats.TemperatureC = RawTemperatureToC(Get(fields, "Temperature"));
            stats.VoltageV = RawVoltageToV(Get(fields, "SupplyVoltage"));
            stats.BiasMa = RawBiasToMa(Get(fields, "TxBiasCurrent"));
        }

        stats.PonType = linkSta switch
        {
            "1" => "GPON",
            "2" => "EPON",
            _ => stats.PonType,
        };

        // trafficstate is the page's own "Connected" test. It carries no O-state, so a link
        // that is not up reads Unknown rather than a guessed activation state.
        if (Get(fields, "trafficstate") is { } traffic)
        {
            var up = traffic.Equals("up", StringComparison.OrdinalIgnoreCase);
            stats.PonLinkStatus = up ? PonLinkState.Operation : PonLinkState.Unknown;
            stats.OperationalStatus = up ? "Up" : "Down";
            stats.LinkState = up ? PonLinkState.Operation.ToDisplayString() : "Down";
        }

        return true;
    }

    /// <summary>Identity read from sta-device.asp. Any value may be missing.</summary>
    internal sealed record DeviceInfo(string? GponSn, string? HardwareVersion, string? SoftwareVersion);

    /// <summary>
    /// Reads the GPON SN, Hardware Version, and Software Version rows of sta-device.asp. The
    /// stDeviceInfo(...) call on the same page holds template defaults and is ignored.
    /// </summary>
    internal static DeviceInfo ParseDevicePage(string html) => new(
        ReadTableRow(html, "GPON SN"),
        ReadTableRow(html, "Hardware Version"),
        ReadTableRow(html, "Software Version"));

    /// <summary>
    /// Maps the device identity onto <paramref name="stats"/>: SN to VendorSn, hardware version to
    /// VendorPn (as the Telekom provider does). OntStats has no firmware field outside the PON
    /// detail, and filling that detail would switch on PON charts this device cannot feed.
    /// </summary>
    internal static void ApplyDeviceInfo(DeviceInfo info, OntStats stats)
    {
        if (info.GponSn is { Length: > 0 } sn)
            stats.VendorSn = sn;
        if (info.HardwareVersion is { Length: > 0 } hw)
            stats.VendorPn = hw;
    }

    /// <summary>Reads <c>var ModelName = "...";</c> from content.asp, or null when absent.</summary>
    internal static string? ParseModelName(string html)
    {
        var m = ModelNameRegex().Match(html);
        return m.Success && m.Groups[1].Value.Trim() is { Length: > 0 } model ? model : null;
    }

    /// <summary>SFF-8472 optical power (0.1 uW units) to dBm, rounded to 0.01. Null for no light.</summary>
    internal static double? RawPowerToDbm(string? raw)
    {
        if (ParseRaw(raw) is not { } v || v <= 0)
            return null;
        return Math.Round(10 * Math.Log10(v / 10000.0), 2);
    }

    /// <summary>SFF-8472 temperature (signed 16-bit, 1/256 C) to Celsius, rounded to 0.1.</summary>
    internal static double? RawTemperatureToC(string? raw)
    {
        if (ParseRaw(raw) is not { } v || v > ushort.MaxValue)
            return null;
        var signed = v >= 32768 ? v - 65536 : v;
        return Math.Round(signed / 256.0, 1);
    }

    /// <summary>SFF-8472 supply voltage (100 uV units) to volts, rounded to 0.01.</summary>
    internal static double? RawVoltageToV(string? raw) =>
        ParseRaw(raw) is { } v ? Math.Round(v / 10000.0, 2) : null;

    /// <summary>SFF-8472 TX bias (2 uA units) to milliamps, rounded to 0.01.</summary>
    internal static double? RawBiasToMa(string? raw) =>
        ParseRaw(raw) is { } v ? Math.Round(v * 2 / 1000.0, 2) : null;

    private static long? ParseRaw(string? raw) =>
        long.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private static string? Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) ? v : null;

    private static string? ReadTableRow(string html, string label)
    {
        var m = Regex.Match(html,
            $@">\s*{Regex.Escape(label)}:\s*</TD>\s*<TD[^>]*>\s*([^<&]+?)\s*(?:&nbsp;|</TD>)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static bool IsSignInStatus(HttpStatusCode? status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private async Task<string?> TryGetPageAsync(HttpClient client, string url, OntPollContext context, CancellationToken ct)
    {
        try
        {
            return await GetPageAsync(client, url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Richerlink ONT {Name}: could not read {Url}", context.Name, url);
            return null;
        }
    }

    /// <summary>
    /// The pages declare charset GB2312, which .NET does not ship, so GetStringAsync throws on
    /// them. Every value parsed is ASCII, so the bytes are decoded as Latin-1 instead.
    /// </summary>
    private static async Task<string> GetPageAsync(HttpClient client, string url, CancellationToken ct)
    {
        var bytes = await client.GetByteArrayAsync(url, ct);
        return Encoding.Latin1.GetString(bytes);
    }

    internal static HttpClient CreateClient(IDeviceDialer dialer) =>
        new(new SocketsHttpHandler { ConnectCallback = DeviceHttp.Via(dialer) })
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

    private static string BuildBaseUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }

    [GeneratedRegex(@"(?:\bvar\s+|\bthis\.)(\w+)\s*=\s*'([^']*)'\s*;", RegexOptions.None, 1000)]
    private static partial Regex PonFieldRegex();

    [GeneratedRegex(@"\bvar\s+ModelName\s*=\s*[""']([^""']*)[""']", RegexOptions.None, 1000)]
    private static partial Regex ModelNameRegex();

    [GeneratedRegex(@"type\s*=\s*[""']?password|login|index\.asp", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SignInHintRegex();
}
