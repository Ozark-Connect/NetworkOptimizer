using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for the Nokia (Alcatel-Lucent, vendor ID "ALCL") XS-010X-Q XGS-PON
/// ONT. The GponForm web API is shared across Nokia's box ONTs, so the reported model
/// and XGS-PON type are hardcoded to the XS-010X-Q (the device reports neither its own
/// model nor a line rate); a GPON sibling like the G-010G-Q would need those adjusted.
/// The device serves a plain-HTTP nginx
/// UI whose only data-bearing page is moreinfo.html, backed by a small JSON API:
///
///   1. POST /GponForm/Login_GetConfig (token=token) -> {"nonce":..,"saltval":..}
///   2. cmt = sha256(username + saltval + password), lowercase hex
///   3. POST /GponForm/LoginForm (cmt=..&nonce=..) -> {"login_result":..,"cookieid":..}
///   4. POST /GponForm/getUpdateinfo (token=token) with Cookie: sessionid=&lt;cookieid&gt;
///      -> {"CurrentPonPw","VendorID","VersionID","SerialNum","Mac","ActiveSwVer",
///          "StandbySwVer","RxOptPwr"}
///
/// The device exposes no TX power, temperature, or explicit link state; the receive
/// optical power (RxOptPwr, dBm) is the one health metric it reports. Login credentials
/// default to admin/1234 on these units but are user-configurable.
/// </summary>
public sealed class NokiaXs010xOntProvider : IOntProvider
{
    public string ProviderKey => "nokia-xs010x-q";
    public string DisplayName => "Nokia XS-010X-Q (HTTP)";

    private const int TimeoutSeconds = 15;
    private const string LoginConfigPath = "/GponForm/Login_GetConfig";
    private const string LoginPath = "/GponForm/LoginForm";
    private const string UpdateInfoPath = "/GponForm/getUpdateinfo";
    private const string FormContentType = "application/x-www-form-urlencoded";

    private readonly ILogger<NokiaXs010xOntProvider> _logger;

    public NokiaXs010xOntProvider(ILogger<NokiaXs010xOntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Nokia XS-010X-Q ONT poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            using var client = CreateClient();
            var baseUrl = BuildBaseUrl(context);

            OntStats? stats = null;
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var cookieId = await LoginAsync(client, baseUrl, context, cancellationToken);
                if (cookieId is not null)
                {
                    var infoJson = await GetUpdateInfoAsync(client, baseUrl, cookieId, context.Name, cancellationToken);
                    stats = new OntStats
                    {
                        Timestamp = DateTime.UtcNow,
                        DeviceHost = context.ConfiguredHost ?? context.Host,
                        DeviceName = context.Name,
                        DeviceModel = "Nokia XS-010X-Q",
                    };
                    ApplyUpdateInfo(infoJson, stats);
                    if (stats.RxPowerDbm is not null)
                        break;
                }

                // The device sometimes returns an unauthenticated/empty response when the login
                // and data request race on the same client - the browser flow and the working
                // curl script both hit fresh connections with pauses between steps. A fresh login
                // on a fresh connection (ConnectionClose in CreateClient) usually settles it.
                if (attempt < maxAttempts)
                {
                    _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: no RX power on attempt {Attempt}/{Max}, retrying",
                        context.Name, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
                }
            }

            if (stats is null)
            {
                _logger.LogWarning("Nokia XS-010X-Q ONT {Name}: login failed", context.Name);
                return null;
            }

            _logger.LogDebug(
                "Nokia XS-010X-Q ONT {Name} polled: Rx={Rx} dBm, SN={Sn}, Link={Link}",
                context.Name, stats.RxPowerDbm?.ToString("F1") ?? "-",
                stats.VendorSn ?? "-", stats.LinkState ?? "-");

            return stats;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Nokia XS-010X-Q ONT {Name} at {Host}",
                context.Name, context.ConfiguredHost ?? context.Host);
            return null;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            using var client = CreateClient();
            var baseUrl = BuildBaseUrl(context);

            var cookieId = await LoginAsync(client, baseUrl, context, cancellationToken);
            if (cookieId is null)
                return (false, "Login failed - check username/password (default is admin/1234)");

            var infoJson = await GetUpdateInfoAsync(client, baseUrl, cookieId, context.Name, cancellationToken);

            var stats = new OntStats();
            ApplyUpdateInfo(infoJson, stats);

            if (stats.RxPowerDbm is null)
                return (false, "Logged in but response did not contain the expected RxOptPwr field");

            return (true, $"Connected (HTTP) - Nokia XS-010X-Q, RX: {stats.RxPowerDbm.Value:F1} dBm");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the three-step GponForm login and returns the session cookie id, or null if
    /// authentication fails. The cookie is delivered inside the LoginForm JSON body (the
    /// page's script clears the Set-Cookie header), so it is threaded back manually as a
    /// Cookie header on the data request rather than via a CookieContainer.
    /// </summary>
    private async Task<string?> LoginAsync(
        HttpClient client, string baseUrl, OntPollContext context, CancellationToken ct)
    {
        var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
        var password = context.Password ?? "";

        string configJson;
        int configStatus;
        using (var content = new StringContent("token=token", Encoding.UTF8, FormContentType))
        using (var response = await client.PostAsync($"{baseUrl}{LoginConfigPath}", content, ct))
        {
            configStatus = (int)response.StatusCode;
            configJson = await response.Content.ReadAsStringAsync(ct);
        }

        var (nonce, saltval) = ParseLoginConfig(configJson);
        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: Login_GetConfig HTTP {Status}, nonce={HasNonce}, saltval='{Salt}', body={Body}",
            context.Name, configStatus, !string.IsNullOrEmpty(nonce), saltval ?? "", Preview(configJson));
        if (string.IsNullOrEmpty(nonce))
            return null;

        var cmt = ComputeCmt(username, saltval ?? "", password);
        var body = $"cmt={cmt}&nonce={Uri.EscapeDataString(nonce)}";

        string loginJson;
        int loginStatus;
        using (var content = new StringContent(body, Encoding.UTF8, FormContentType))
        using (var response = await client.PostAsync($"{baseUrl}{LoginPath}", content, ct))
        {
            loginStatus = (int)response.StatusCode;
            loginJson = await response.Content.ReadAsStringAsync(ct);
        }

        var cookieId = ParseCookieId(loginJson);
        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: LoginForm HTTP {Status}, gotCookie={HasCookie}, body={Body}",
            context.Name, loginStatus, cookieId != null, Preview(loginJson));
        return cookieId;
    }

    private async Task<string> GetUpdateInfoAsync(
        HttpClient client, string baseUrl, string cookieId, string deviceName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{UpdateInfoPath}")
        {
            Content = new StringContent("token=token", Encoding.UTF8, FormContentType),
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"sessionid={cookieId}");

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: getUpdateinfo HTTP {Status}, body={Body}",
            deviceName, (int)response.StatusCode, Preview(body));
        return body;
    }

    /// <summary>Trims a raw device response for diagnostic logging.</summary>
    private static string Preview(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length > 800 ? s[..800] + "...(truncated)" : s);

    /// <summary>
    /// cmt = sha256(username + saltval + password) as lowercase hex. Verified against a live
    /// unit: sha256("admin" + "ea" + "1234") == b7290cb3...f22fc7.
    /// </summary>
    internal static string ComputeCmt(string username, string saltval, string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(username + saltval + password));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Extracts nonce and saltval from the Login_GetConfig JSON response.</summary>
    internal static (string? Nonce, string? Salt) ParseLoginConfig(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            return (GetStringProp(doc.RootElement, "nonce"), GetStringProp(doc.RootElement, "saltval"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Reads the session cookie id from the LoginForm JSON response. Returns null when the
    /// device reports a login error ({"login_result":"error"}) or omits the cookie.
    /// </summary>
    internal static string? ParseCookieId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var result = GetStringProp(doc.RootElement, "login_result");
            if (string.Equals(result, "error", StringComparison.OrdinalIgnoreCase))
                return null;

            var cookieId = GetStringProp(doc.RootElement, "cookieid");
            return string.IsNullOrWhiteSpace(cookieId) ? null : cookieId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the getUpdateinfo JSON onto OntStats. Only RxOptPwr, VendorID, VersionID and
    /// SerialNum carry monitoring value; the device reports no TX power, temperature, or
    /// explicit link state, so operational status is inferred from a present RX reading.
    /// </summary>
    internal static void ApplyUpdateInfo(string json, OntStats stats)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            var root = doc.RootElement;

            stats.RxPowerDbm = ParseDouble(GetStringProp(root, "RxOptPwr")) ?? stats.RxPowerDbm;

            if (GetStringProp(root, "VendorID") is { Length: > 0 } vendor)
                stats.VendorName = vendor;

            if (GetStringProp(root, "VersionID") is { Length: > 0 } version)
                stats.VendorPn = version;

            if (GetStringProp(root, "SerialNum") is { Length: > 0 } serial)
                stats.VendorSn = serial;

            // XS-010X-Q is a 10G-symmetric XGS-PON ONT; the device exposes no line rate,
            // so the PON type is taken from the model rather than derived from a rate field.
            stats.PonType = "XGS-PON";

            // No link-state field is exposed. A successful authenticated read that returns a
            // real optical-power value means the ONU is powered and seeing downstream light,
            // which we surface as Up; without an RxOptPwr reading we leave status unknown.
            if (stats.RxPowerDbm is not null)
            {
                stats.OperationalStatus = "Up";
                stats.LinkState = "Up";
            }
        }
        catch (JsonException) { }
    }

    private static string? GetStringProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : null;

    internal static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        // Mirror the working curl flow: a fresh TCP connection per request. These GponForm
        // boxes can tie the login session to the connection, so keep-alive reuse across the
        // login -> getUpdateinfo steps can return an empty/unauthenticated response.
        client.DefaultRequestHeaders.ConnectionClose = true;
        return client;
    }

    private static string BuildBaseUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }
}
