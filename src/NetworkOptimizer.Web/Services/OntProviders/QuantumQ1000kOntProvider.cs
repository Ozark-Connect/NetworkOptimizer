using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for the Quantum Fiber Q1000K SmartNID (CenturyLink/Quantum Fiber GPON).
/// The device exposes a React web UI backed by a CGI JSON API. Authentication is a
/// form POST to /cgi/cgi_action which returns a Session-Id cookie; data is then read
/// from /cgi/cgi_get?Object=... endpoints that return TR-181-style parameter objects.
///
/// The Device.Optical.Interface.1 object exposes full DDM optics (Rx/Tx power,
/// temperature, voltage, bias current), link status, PON line rate, and BIP error
/// counts, alongside device identity from Device.DeviceInfo.
/// </summary>
public sealed class QuantumQ1000kOntProvider : IOntProvider
{
    public string ProviderKey => "quantum-q1000k";
    public string DisplayName => "Quantum Fiber Q1000K (HTTP)";

    private const int TimeoutSeconds = 15;
    private const string LoginPath = "/cgi/cgi_action";
    private const string ConnectionStatusPath = "/cgi/cgi_get?Object=GetConnectionStatus";
    private const string DeviceInfoPath =
        "/cgi/cgi_get?Object=Device.DeviceInfo&SoftwareVersion=&ModelName=&HardwareVersion=&SerialNumber=";
    private const string OpticalPath = "/cgi/cgi_get?Object=Device.Optical.Interface.1";

    private readonly ILogger<QuantumQ1000kOntProvider> _logger;

    public QuantumQ1000kOntProvider(ILogger<QuantumQ1000kOntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Quantum Q1000K ONT poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            var (baseUrl, client, handler) = await ResolveBaseUrlAsync(context, cancellationToken);
            using var _ = handler;
            using var __ = client;

            if (!await LoginAsync(client, baseUrl, context, cancellationToken))
            {
                _logger.LogWarning("Quantum Q1000K ONT {Name}: login failed", context.Name);
                return null;
            }

            var stats = new OntStats
            {
                Timestamp = DateTime.UtcNow,
                DeviceHost = context.Host,
                DeviceName = context.Name,
                DeviceModel = "Quantum Q1000K",
            };

            var connectionJson = await client.GetStringAsync($"{baseUrl}{ConnectionStatusPath}", cancellationToken);
            ApplyConnectionStatus(connectionJson, stats);

            var deviceInfoJson = await client.GetStringAsync($"{baseUrl}{DeviceInfoPath}", cancellationToken);
            ApplyDeviceInfo(deviceInfoJson, stats);

            var opticalJson = await client.GetStringAsync($"{baseUrl}{OpticalPath}", cancellationToken);
            ApplyOpticalInterface(opticalJson, stats);

            _logger.LogDebug(
                "Quantum Q1000K ONT {Name} polled: Rx={Rx} dBm, Tx={Tx} dBm, Temp={Temp} C, Link={Link}, {PonType}",
                context.Name, stats.RxPowerDbm?.ToString("F2") ?? "-",
                stats.TxPowerDbm?.ToString("F2") ?? "-",
                stats.TemperatureC?.ToString("F0") ?? "-",
                stats.LinkState ?? "-", stats.PonType ?? "-");

            return stats;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Quantum Q1000K ONT {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
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
            var (baseUrl, client, handler) = await ResolveBaseUrlAsync(context, cancellationToken);
            using var _ = handler;
            using var __ = client;

            if (!await LoginAsync(client, baseUrl, context, cancellationToken))
                return (false, "Login failed - check username/password (the admin password is printed on the device)");

            var deviceInfoJson = await client.GetStringAsync($"{baseUrl}{DeviceInfoPath}", cancellationToken);
            if (!deviceInfoJson.Contains("ModelName", StringComparison.OrdinalIgnoreCase))
                return (false, "Connected but response does not contain expected device info fields");

            var stats = new OntStats { DeviceModel = "Quantum Q1000K" };
            ApplyDeviceInfo(deviceInfoJson, stats);

            var opticalJson = await client.GetStringAsync($"{baseUrl}{OpticalPath}", cancellationToken);
            ApplyOpticalInterface(opticalJson, stats);

            var scheme = baseUrl.StartsWith("https") ? "HTTPS" : "HTTP";
            var rx = stats.RxPowerDbm.HasValue ? $", RX: {stats.RxPowerDbm.Value:F2} dBm" : "";
            return (true, $"Connected ({scheme}) - {stats.DeviceModel}{rx}");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"SSL connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Form login: POST username/password to /cgi/cgi_action as a raw text/plain body,
    /// replicating exactly what the device's React UI sends (content type included) since
    /// that is the only request form we have confirmed yields a session. The device replies
    /// with a Session-Id cookie stored by the CookieContainer. Unauthenticated /cgi/cgi_get
    /// reads are rejected (HTTP 444), so we confirm success by probing GetConnectionStatus
    /// and checking for the wan_status payload.
    /// </summary>
    private async Task<bool> LoginAsync(
        HttpClient client, string baseUrl, OntPollContext context, CancellationToken ct)
    {
        var username = context.Username ?? "admin";
        var password = context.Password ?? "";

        var body = $"username={username}&password={password}";
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");

        try { using var login = await client.PostAsync($"{baseUrl}{LoginPath}", content, ct); }
        catch (HttpRequestException) { return false; }

        try
        {
            var probe = await client.GetStringAsync($"{baseUrl}{ConnectionStatusPath}", ct);
            return probe.Contains("wan_status", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses GetConnectionStatus: {"wan_status":{"link_state":"connected","net_state":"bridged",
    /// "uplink_rate":"1244000","downlink_rate":"2488000",...}}. Rates are in kbps. Sets the link
    /// health (Up/Down) and derives the PON type from the downstream line rate.
    /// </summary>
    internal static void ApplyConnectionStatus(string json, OntStats stats)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("wan_status", out var wan))
                return;

            if (wan.TryGetProperty("link_state", out var linkEl))
            {
                var connected = string.Equals(linkEl.GetString(), "connected", StringComparison.OrdinalIgnoreCase);
                stats.OperationalStatus = connected ? "Up" : "Down";
                stats.LinkState = connected ? "Up" : "Down";
            }

            var downlinkKbps = ParseLong(GetStringProp(wan, "downlink_rate"));
            if (downlinkKbps.HasValue)
                stats.PonType = downlinkKbps.Value >= 9_000_000 ? "XGS-PON" : "GPON";
        }
        catch (JsonException) { }
    }

    /// <summary>
    /// Parses Device.DeviceInfo parameters (ModelName, SerialNumber, SoftwareVersion).
    /// ModelName drives the displayed model/part number; SerialNumber maps to the SN field.
    /// </summary>
    internal static void ApplyDeviceInfo(string json, OntStats stats)
    {
        var p = ParseParamObject(json);

        if (p.TryGetValue("ModelName", out var model) && !string.IsNullOrWhiteSpace(model))
        {
            stats.DeviceModel = $"Quantum {model}";
            stats.VendorPn = model;
        }

        if (p.TryGetValue("SerialNumber", out var serial) && !string.IsNullOrWhiteSpace(serial))
            stats.VendorSn = serial;
    }

    /// <summary>
    /// Parses the full Device.Optical.Interface.1 object (and its Stats sub-object).
    /// DDM optics power/voltage/bias are reported in milli-units (0.001 dBm, mV, µA);
    /// temperature is whole degrees Celsius. The standard TR-181 Status field
    /// ("Up"/"Down") drives link health, with the X_AXON_LineStatus (GOOD/BAD) flag as
    /// a fallback. The downstream line rate (Mbps) confirms the PON variant.
    /// </summary>
    internal static void ApplyOpticalInterface(string json, OntStats stats)
    {
        var p = ParseParamObject(json);

        stats.RxPowerDbm = ParseMilli(GetValue(p, "OpticalSignalLevel")) ?? stats.RxPowerDbm;
        stats.TxPowerDbm = ParseMilli(GetValue(p, "TransmitOpticalLevel")) ?? stats.TxPowerDbm;
        stats.VoltageV = ParseMilli(GetValue(p, "X_CTL_Voltage")) ?? stats.VoltageV;
        stats.BiasMa = ParseMilli(GetValue(p, "X_CTL_BiasCurrent")) ?? stats.BiasMa;

        if (ParseDouble(GetValue(p, "X_CTL_Temperature")) is { } temp)
            stats.TemperatureC = temp;

        if (ParseLong(GetValue(p, "X_CTL_BIPErrorsReceived")) is { } bip)
            stats.BipErrors = bip;

        var status = GetValue(p, "Status");
        if (!string.IsNullOrWhiteSpace(status))
        {
            stats.LinkState = status;
            stats.OperationalStatus = status;
        }
        else if (p.TryGetValue("X_AXON_LineStatus", out var line) && !string.IsNullOrWhiteSpace(line))
        {
            var good = string.Equals(line, "GOOD", StringComparison.OrdinalIgnoreCase);
            stats.LinkState ??= good ? "Up" : "Down";
            stats.OperationalStatus ??= good ? "Up" : "Down";
        }

        if (ParseLong(GetValue(p, "X_AXON_DownstreamRate")) is { } downMbps)
            stats.PonType = downMbps >= 9000 ? "XGS-PON" : "GPON";

        stats.LinkUptimeSeconds = ParseLong(GetValue(p, "X_AXON_LinkUpTime")) ?? stats.LinkUptimeSeconds;

        var oltVendor = GetValue(p, "X_CTL_OLTVendor");
        if (!string.IsNullOrWhiteSpace(oltVendor)) stats.OltVendor = oltVendor;

        var oltModel = GetValue(p, "X_CTL_OLTModel");
        if (!string.IsNullOrWhiteSpace(oltModel)) stats.OltModel = oltModel;
    }

    /// <summary>
    /// Flattens the TR-181-style response {"Objects":[{"Param":[{"ParamName":..,"ParamValue":..}]}]}
    /// into a single name/value dictionary, merging the parameters of every object in the
    /// array (the optical endpoint splits its data across Interface.1 and Interface.1.Stats).
    /// </summary>
    private static Dictionary<string, string> ParseParamObject(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Objects", out var objects)
                || objects.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var obj in objects.EnumerateArray())
            {
                if (!obj.TryGetProperty("Param", out var pars) || pars.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var par in pars.EnumerateArray())
                {
                    var name = GetStringProp(par, "ParamName");
                    var value = GetStringProp(par, "ParamValue");
                    if (!string.IsNullOrEmpty(name) && value != null)
                        result[name] = value;
                }
            }
        }
        catch (JsonException) { }

        return result;
    }

    private static string? GetStringProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? GetValue(Dictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) ? v : null;

    private static long? ParseLong(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val) ? val : null;

    private static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : null;

    /// <summary>Parses an integer reported in milli-units (0.001) into its base unit.</summary>
    private static double? ParseMilli(string? text) =>
        ParseLong(text) is { } v ? v / 1000.0 : null;

    /// <summary>
    /// Tries the port-based scheme first (HTTPS for 443, HTTP for 80), then falls back to the
    /// opposite scheme. All HTTPS uses self-signed cert bypass since these are local devices.
    /// </summary>
    private async Task<(string BaseUrl, HttpClient Client, HttpClientHandler Handler)> ResolveBaseUrlAsync(
        OntPollContext context, CancellationToken ct)
    {
        var port = context.Port > 0 ? context.Port : 443;
        var primaryScheme = port == 80 ? "http" : "https";
        var fallbackScheme = primaryScheme == "https" ? "http" : "https";

        var primaryUrl = BuildBaseUrl(context.Host, port, primaryScheme);
        var (handler, client) = CreateHttpClient();

        try
        {
            using var response = await client.GetAsync($"{primaryUrl}/", ct);
            return (primaryUrl, client, handler);
        }
        catch (HttpRequestException ex) when (
            ex.InnerException is System.Security.Authentication.AuthenticationException
            || ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("{Scheme} failed with SSL error for {Host}, trying {Fallback}",
                primaryScheme.ToUpperInvariant(), context.Host, fallbackScheme.ToUpperInvariant());
        }
        catch (HttpRequestException)
        {
            _logger.LogDebug("{Scheme} connection failed for {Host}, trying {Fallback}",
                primaryScheme.ToUpperInvariant(), context.Host, fallbackScheme.ToUpperInvariant());
        }

        client.Dispose();
        handler.Dispose();

        var fallbackUrl = BuildBaseUrl(context.Host, port, fallbackScheme);
        var (handler2, client2) = CreateHttpClient();

        try
        {
            using var probe = await client2.GetAsync($"{fallbackUrl}/", ct);
            _logger.LogInformation("Quantum Q1000K ONT {Host} reachable via {Scheme}",
                context.Host, fallbackScheme.ToUpperInvariant());
            return (fallbackUrl, client2, handler2);
        }
        catch
        {
            client2.Dispose();
            handler2.Dispose();
            throw;
        }
    }

    private static (HttpClientHandler Handler, HttpClient Client) CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        return (handler, client);
    }

    private static string BuildBaseUrl(string host, int port, string scheme)
    {
        var portSuffix = (scheme == "http" && port == 80) || (scheme == "https" && port == 443)
            ? "" : $":{port}";
        return $"{scheme}://{host}{portSuffix}";
    }
}
