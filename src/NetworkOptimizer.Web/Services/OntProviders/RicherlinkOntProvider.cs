using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for the Richerlink RL821GWV-D GPON ONT (Airtel India FTTH), polled in bridge
/// mode. Its China Telecom "e8"-style web UI renders the PON optics into inline script on
/// /cgi-bin/sta-network.asp as raw SFF-8472 DDM words, which this provider converts.
/// <para>
/// The ONT allows one signed-in web session at a time, so a poll signs in, reads its pages, and
/// signs out again. A session that is already open for the gateway's address (the ONT sees every
/// LAN client through the monitoring interface's SNAT) serves the pages without a sign-in; the
/// poll then leaves it open. Three failed sign-ins lock the ONT's web page, so a rejected sign-in
/// holds further tries with the same credentials for <see cref="RejectedSignInHold"/>.
/// </para>
/// <para>
/// Every request is a raw HTTP/1.0 exchange (<see cref="LenientHttp"/>): the ONT's Boa 0.94
/// server sends loose headers, and the reporter's working script talks to it the same way.
/// </para>
/// </summary>
[VendorSpecific("Richerlink", "e8-style /cgi-bin/*.asp pages: PonInfoClass script literals in raw SFF-8472 units; index2.asp form sign-in")]
public sealed partial class RicherlinkOntProvider : IOntProvider
{
    public string ProviderKey => "richerlink-ont";
    public string DisplayName => "Richerlink RL821GWV-D (HTTP)";

    internal const string DefaultModel = "RL821GWV-D";

    /// <summary>How long a rejected sign-in holds the next try with the same credentials.</summary>
    internal static readonly TimeSpan RejectedSignInHold = TimeSpan.FromMinutes(5);

    private const int TimeoutSeconds = 10;
    private const string NetworkPath = "/cgi-bin/sta-network.asp";
    private const string DevicePath = "/cgi-bin/sta-device.asp";
    private const string ContentPath = "/cgi-bin/content.asp";
    internal const string SignInPath = "/cgi-bin/index2.asp";
    internal const string SignOutPath = "/cgi-bin/logout.cgi";

    // Each page counts as data only when it carries the marker its parser reads.
    private const string NetworkMarker = "PonInfoClass";
    private const string DeviceMarker = "GPON SN";
    private const string ContentMarker = "ModelName";

    private readonly ILogger<RicherlinkOntProvider> _logger;

    /// <summary>Rejected sign-ins by <see cref="OntPollContext.CacheKey"/>, with a hash of the credentials tried.</summary>
    private readonly ConcurrentDictionary<string, (int Credentials, DateTime Until)> _rejectedSignIns = new();

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

    /// <summary>A raw response: status code and body.</summary>
    internal sealed record Page(int Status, string Body);

    /// <summary>The network page, or why it could not be read, and whether a sign-in was sent.</summary>
    private sealed record NetworkFetch(string? Html, bool SignInSent, string? Failure);

    /// <summary>The failure text for a device that asks for a sign-in with no credentials configured.</summary>
    internal static string CredentialsNeededMessage(string host) =>
        $"{HostOrDevice(host)} requires sign-in. Enter the username and password you use on the ONT's web page.";

    /// <summary>The failure text for a sign-in the ONT did not accept.</summary>
    internal static string SignInRejectedMessage(string host) =>
        $"{HostOrDevice(host)} did not accept the sign-in. Check the username and password, and that no " +
        "browser is signed in to the ONT's web page (it allows one session at a time).";

    /// <summary>The failure text while a rejected sign-in holds further tries.</summary>
    internal static string SignInHeldMessage(string host, TimeSpan remaining) =>
        $"{SignInRejectedMessage(host)} The next sign-in is in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} min, " +
        "so the ONT does not lock its web page after three failures.";

    private static string HostOrDevice(string host) => string.IsNullOrWhiteSpace(host) ? "The device" : host;

    public async Task<PollResult<OntStats>> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Richerlink ONT poll requested but Host is empty (config {Id})", context.Id);
            return PollResult<OntStats>.Failed("No address is configured for this device.");
        }

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        var signInSent = false;
        try
        {
            var fetch = await FetchNetworkPageAsync(context, cookies, cancellationToken);
            signInSent = fetch.SignInSent;
            if (fetch.Html == null)
            {
                _logger.LogWarning("Richerlink ONT {Name} at {Host}: {Failure}", context.Name, context.Host, fetch.Failure);
                return PollResult<OntStats>.Failed(fetch.Failure!);
            }

            var stats = new OntStats
            {
                Timestamp = DateTime.UtcNow,
                DeviceHost = context.Host,
                DeviceName = context.Name,
                DeviceModel = DefaultModel,
                VendorName = "Richerlink",
            };
            ApplyNetworkPage(fetch.Html, stats);

            // Identity pages are best-effort: the optics above are the poll, these only label it.
            var deviceHtml = await TryGetPageAsync(context, DevicePath, cookies, cancellationToken);
            if (deviceHtml != null && ClassifyPage(deviceHtml, DeviceMarker) == PageKind.Data)
                ApplyDeviceInfo(ParseDevicePage(deviceHtml), stats);

            var contentHtml = await TryGetPageAsync(context, ContentPath, cookies, cancellationToken);
            if (contentHtml != null && ParseModelName(contentHtml) is { } model)
                stats.DeviceModel = model;

            _logger.LogDebug(
                "Richerlink ONT {Name} polled: Rx={Rx} dBm, Tx={Tx} dBm, Link={Link}",
                context.Name, stats.RxPowerDbm?.ToString("F2") ?? "-",
                stats.TxPowerDbm?.ToString("F2") ?? "-", stats.LinkState ?? "-");

            return PollResult<OntStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Richerlink ONT {Name} at {Host}", context.Name, context.Host);
            return PollResult<OntStats>.Failed(HttpFailureSummary.Describe(ex, context.Host));
        }
        finally
        {
            if (signInSent)
                await SignOutAsync(context, cookies);
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        var signInSent = false;
        try
        {
            var fetch = await FetchNetworkPageAsync(context, cookies, cancellationToken);
            signInSent = fetch.SignInSent;
            if (fetch.Html == null)
                return (false, fetch.Failure!);

            var stats = new OntStats();
            ApplyNetworkPage(fetch.Html, stats);
            return (true,
                $"Connected (HTTP) - RX: {stats.RxPowerDbm?.ToString("F2") ?? "?"} dBm, TX: {stats.TxPowerDbm?.ToString("F2") ?? "?"} dBm");
        }
        catch (Exception ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.Host));
        }
        finally
        {
            if (signInSent)
                await SignOutAsync(context, cookies);
        }
    }

    /// <summary>
    /// Reads sta-network.asp, signing in first only when the ONT asks for it. Credentials that
    /// were just rejected are not sent again until <see cref="RejectedSignInHold"/> has passed.
    /// </summary>
    private async Task<NetworkFetch> FetchNetworkPageAsync(
        OntPollContext context, Dictionary<string, string> cookies, CancellationToken ct)
    {
        var page = await RequestAsync(context, "GET", NetworkPath, cookies, formBody: null, ct);
        switch (Classify(page, NetworkMarker))
        {
            case PageKind.Data:
                return new(page.Body, SignInSent: false, null);
            case PageKind.Unrecognized:
                return new(null, false, Unrecognized(page, context.Host));
        }

        if (string.IsNullOrEmpty(context.Username) || string.IsNullOrEmpty(context.Password))
            return new(null, false, CredentialsNeededMessage(context.Host));

        var credentials = HashCode.Combine(context.Username, context.Password);
        if (_rejectedSignIns.TryGetValue(context.CacheKey, out var rejected)
            && rejected.Credentials == credentials && rejected.Until > DateTime.UtcNow)
        {
            return new(null, false, SignInHeldMessage(context.Host, rejected.Until - DateTime.UtcNow));
        }

        await SignInAsync(context, cookies, ct);
        page = await RequestAsync(context, "GET", NetworkPath, cookies, formBody: null, ct);
        switch (Classify(page, NetworkMarker))
        {
            case PageKind.Data:
                _rejectedSignIns.TryRemove(context.CacheKey, out _);
                return new(page.Body, SignInSent: true, null);
            case PageKind.SignIn:
                _rejectedSignIns[context.CacheKey] = (credentials, DateTime.UtcNow + RejectedSignInHold);
                return new(null, true, SignInRejectedMessage(context.Host));
            default:
                return new(null, true, Unrecognized(page, context.Host));
        }
    }

    private static string Unrecognized(Page page, string host) =>
        HttpFailureSummary.ForResponse(page.Status is 200 or 0 ? null : (HttpStatusCode)page.Status, host);

    /// <summary>
    /// Does what the login page's SubmitForm() does: load index2.asp for a SESSIONID, set the
    /// UID/PSW/LoginTimes cookies its script sets, and post its form fields back to index2.asp.
    /// </summary>
    private async Task SignInAsync(OntPollContext context, Dictionary<string, string> cookies, CancellationToken ct)
    {
        await RequestAsync(context, "GET", SignInPath, cookies, formBody: null, ct);
        cookies["UID"] = context.Username!;
        cookies["PSW"] = context.Password!;
        cookies["LoginTimes"] = "1";
        await RequestAsync(context, "POST", SignInPath, cookies, BuildSignInForm(context.Username!, context.Password!), ct);
    }

    /// <summary>Signs out so the ONT's one web session is free again. Best-effort: a failure is only logged.</summary>
    private async Task SignOutAsync(OntPollContext context, Dictionary<string, string> cookies)
    {
        try
        {
            await RequestAsync(context, "GET", SignOutPath, cookies, formBody: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Richerlink ONT {Name} at {Host}: sign-out failed", context.Name, context.Host);
        }
    }

    /// <summary>The login page's form fields, in its order, URL-encoded.</summary>
    internal static string BuildSignInForm(string username, string password)
    {
        var fields = new (string Name, string Value)[]
        {
            ("Username", username), ("Logoff", "0"), ("hLoginTimes", "1"), ("hLoginTimes_Zero", "0"),
            ("value_one", "1"), ("Password1", password), ("Password2", password), ("logintype", "usr"),
            ("LoginFailTime", "1"), ("hLoginFailTime", "1"), ("Password", password),
        };
        return string.Join("&", fields.Select(f => $"{f.Name}={Uri.EscapeDataString(f.Value)}"));
    }

    /// <summary>Classifies a response: 401/403 is a sign-in, any other non-200 is unrecognized.</summary>
    internal static PageKind Classify(Page page, string dataMarker) => page.Status switch
    {
        401 or 403 => PageKind.SignIn,
        200 => ClassifyPage(page.Body, dataMarker),
        _ => PageKind.Unrecognized,
    };

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

    private async Task<string?> TryGetPageAsync(
        OntPollContext context, string path, Dictionary<string, string> cookies, CancellationToken ct)
    {
        try
        {
            var page = await RequestAsync(context, "GET", path, cookies, formBody: null, ct);
            return page.Status == 200 ? page.Body : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Richerlink ONT {Name}: could not read {Path}", context.Name, path);
            return null;
        }
    }

    /// <summary>
    /// One raw HTTP/1.0 exchange through the context's dialer. Cookies in <paramref name="cookies"/>
    /// are sent, and any the response sets are added to it. The pages declare charset GB2312,
    /// which .NET does not ship; every value parsed is ASCII, so the decoding does not matter.
    /// </summary>
    private static async Task<Page> RequestAsync(
        OntPollContext context, string method, string path, Dictionary<string, string> cookies,
        string? formBody, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        var cookieHeader = cookies.Count == 0 ? null : string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}"));
        byte[] raw;
        try
        {
            raw = await LenientHttp.RawHttpRequestAsync(
                context.Dialer, context.Host, context.Port > 0 ? context.Port : 80, method, path,
                authHeader: null, cookieHeader, formBody, timeout.Token);
        }
        catch (SocketException ex)
        {
            // Wrapped as HttpClient would, so HttpFailureSummary names refused, unresolved, and timed out.
            throw new HttpRequestException(ex.Message, ex);
        }

        var (headerEnd, _) = LenientHttp.FindHeaderBodySplit(raw, raw.Length);
        foreach (Match m in SetCookieRegex().Matches(Encoding.Latin1.GetString(raw, 0, headerEnd)))
            cookies[m.Groups[1].Value] = m.Groups[2].Value;

        var response = LenientHttp.ParseRawHttpResponse(raw, raw.Length);
        return new Page(response.StatusCode, response.Body);
    }

    [GeneratedRegex(@"(?:\bvar\s+|\bthis\.)(\w+)\s*=\s*'([^']*)'\s*;", RegexOptions.None, 1000)]
    private static partial Regex PonFieldRegex();

    [GeneratedRegex(@"\bvar\s+ModelName\s*=\s*[""']([^""']*)[""']", RegexOptions.None, 1000)]
    private static partial Regex ModelNameRegex();

    [GeneratedRegex(@"type\s*=\s*[""']?password|login|index\.asp", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex SignInHintRegex();

    [GeneratedRegex(@"(?im)^Set-Cookie:\s*([^=;\s]+)=([^;\r\n]*)", RegexOptions.None, 1000)]
    private static partial Regex SetCookieRegex();
}
