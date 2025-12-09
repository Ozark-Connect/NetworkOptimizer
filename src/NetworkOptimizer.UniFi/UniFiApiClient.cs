using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi.Models;
using Polly;
using Polly.Retry;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Full-featured UniFi Controller API client with cookie-based authentication
/// Handles all quirks of the unofficial UniFi API including:
/// - Cookie-based session management (like browser)
/// - CSRF token handling
/// - Automatic re-authentication on 401/403
/// - Retry logic with Polly
/// - Self-signed certificate handling
/// - UniFi OS (UDM/UCG) vs standalone controller path detection
///
/// For UniFi OS devices (UDM, UCG), the Network Application is proxied at:
///   /proxy/network/api/s/{site}/...
/// For standalone controllers, the path is:
///   /api/s/{site}/...
/// </summary>
public class UniFiApiClient : IDisposable
{
    private readonly ILogger<UniFiApiClient> _logger;
    private readonly string _controllerUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly string _site;
    private HttpClient? _httpClient;
    private CookieContainer? _cookieContainer;
    private string? _csrfToken;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private bool _isAuthenticated = false;
    private bool _isUniFiOs = false; // True for UDM/UCG, false for standalone controller
    private bool _pathDetected = false;

    public UniFiApiClient(
        ILogger<UniFiApiClient> logger,
        string controllerHost,
        string username,
        string password,
        string site = "default")
    {
        _logger = logger;
        _controllerUrl = controllerHost.StartsWith("https://") ? controllerHost : $"https://{controllerHost}";
        _username = username;
        _password = password;
        _site = site;

        // Configure retry policy for transient failures
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timespan, retryCount, context) =>
                {
                    _logger.LogWarning("Retry {RetryCount} after {Timespan}s due to {Exception}",
                        retryCount, timespan.TotalSeconds, exception.Message);
                });

        InitializeHttpClient();
    }

    private void InitializeHttpClient()
    {
        _cookieContainer = new CookieContainer();

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            UseCookies = true,
            // UniFi controllers often use self-signed certificates
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "NetworkOptimizer.UniFi/1.0");
    }

    /// <summary>
    /// Authenticates with the UniFi controller using cookie-based auth (like a browser)
    /// </summary>
    public async Task<bool> LoginAsync(CancellationToken cancellationToken = default)
    {
        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (_isAuthenticated)
            {
                _logger.LogDebug("Already authenticated, skipping login");
                return true;
            }

            _logger.LogInformation("Authenticating with UniFi controller at {Url}", _controllerUrl);

            // Reset client to clear old cookies
            InitializeHttpClient();

            var loginRequest = new UniFiLoginRequest
            {
                Username = _username,
                Password = _password,
                Remember = false,
                Strict = true
            };

            var loginUrl = $"{_controllerUrl}/api/auth/login";
            var content = new StringContent(
                JsonSerializer.Serialize(loginRequest),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient!.PostAsync(loginUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Login failed with status {StatusCode}: {Error}",
                    response.StatusCode, errorBody);
                return false;
            }

            // Extract CSRF token from response headers
            if (response.Headers.TryGetValues("X-Csrf-Token", out var csrfTokens))
            {
                _csrfToken = csrfTokens.FirstOrDefault();
                if (!string.IsNullOrEmpty(_csrfToken))
                {
                    _httpClient.DefaultRequestHeaders.Remove("X-Csrf-Token");
                    _httpClient.DefaultRequestHeaders.Add("X-Csrf-Token", _csrfToken);
                    _logger.LogDebug("CSRF token acquired");
                }
            }

            // Verify cookies were set
            var cookies = _cookieContainer!.GetCookies(new Uri(_controllerUrl));
            var hasCookies = cookies.Count > 0;

            if (!hasCookies)
            {
                _logger.LogWarning("No cookies received after login - authentication may fail");
            }
            else
            {
                _logger.LogDebug("Received {CookieCount} cookies from controller", cookies.Count);
            }

            _isAuthenticated = true;
            _logger.LogInformation("Successfully authenticated with UniFi controller");

            // Detect controller type after successful authentication
            await DetectControllerTypeAsync(cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during login");
            return false;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// Ensures we're authenticated, re-authenticating if necessary
    /// </summary>
    private async Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        if (_isAuthenticated)
            return true;

        return await LoginAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the correct API path based on whether this is UniFi OS or standalone controller
    /// </summary>
    private string BuildApiPath(string endpoint)
    {
        // For UniFi OS (UDM/UCG), APIs are proxied through /proxy/network
        if (_isUniFiOs)
        {
            return $"{_controllerUrl}/proxy/network/api/s/{_site}/{endpoint}";
        }
        // For standalone controllers
        return $"{_controllerUrl}/api/s/{_site}/{endpoint}";
    }

    /// <summary>
    /// Detects whether this is a UniFi OS device (UDM/UCG) or standalone controller
    /// by trying the /proxy/network path first (more common for modern deployments)
    /// </summary>
    private async Task DetectControllerTypeAsync(CancellationToken cancellationToken = default)
    {
        if (_pathDetected)
            return;

        _logger.LogDebug("Detecting controller type (UniFi OS vs standalone)...");

        // Try UniFi OS path first (UDM/UCG) - this is the modern path
        try
        {
            var response = await _httpClient!.GetAsync(
                $"{_controllerUrl}/proxy/network/api/s/{_site}/stat/sysinfo",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _isUniFiOs = true;
                _pathDetected = true;
                _logger.LogInformation("Detected UniFi OS device (UDM/UCG) - using /proxy/network path");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("UniFi OS path test failed: {Message}", ex.Message);
        }

        // Fall back to standalone controller path
        try
        {
            var response = await _httpClient!.GetAsync(
                $"{_controllerUrl}/api/s/{_site}/stat/sysinfo",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _isUniFiOs = false;
                _pathDetected = true;
                _logger.LogInformation("Detected standalone UniFi Controller - using /api path");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Standalone path test failed: {Message}", ex.Message);
        }

        // Default to UniFi OS path if detection fails (most common modern scenario)
        _isUniFiOs = true;
        _pathDetected = true;
        _logger.LogWarning("Could not detect controller type, defaulting to UniFi OS path");
    }

    /// <summary>
    /// Gets whether this is a UniFi OS device (UDM/UCG)
    /// </summary>
    public bool IsUniFiOs => _isUniFiOs;

    /// <summary>
    /// Executes an API call with automatic re-authentication on 401/403
    /// </summary>
    private async Task<T?> ExecuteApiCallAsync<T>(
        Func<Task<HttpResponseMessage>> apiCall,
        CancellationToken cancellationToken = default) where T : class
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            _logger.LogError("Failed to authenticate before API call");
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await apiCall();

            // Handle authentication failures
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Got {StatusCode}, re-authenticating...", response.StatusCode);
                _isAuthenticated = false;

                if (!await LoginAsync(cancellationToken))
                {
                    _logger.LogError("Re-authentication failed");
                    return null;
                }

                // Retry the call with new authentication
                response = await apiCall();
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("API call failed with status {StatusCode}: {Error}",
                    response.StatusCode, errorBody);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return result;
        });
    }

    #region Device Management APIs

    /// <summary>
    /// GET /proxy/network/api/s/{site}/stat/device (UniFi OS) or
    /// GET /api/s/{site}/stat/device (standalone) - Get all UniFi devices
    /// Returns the large device payload with all port profiles, switch port details, etc.
    /// </summary>
    public async Task<List<UniFiDeviceResponse>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching all devices from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiDeviceResponse>>(
            () => _httpClient!.GetAsync(BuildApiPath("stat/device"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogInformation("Retrieved {Count} devices", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve devices or received non-ok response");
        return new List<UniFiDeviceResponse>();
    }

    /// <summary>
    /// GET stat/device - Get all devices as raw JSON string
    /// Used by audit engine which needs the complete raw payload
    /// </summary>
    public async Task<string?> GetDevicesRawJsonAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching raw device JSON from site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(BuildApiPath("stat/device"), cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Retrieved raw device JSON ({Length} bytes)", json.Length);
                return json;
            }

            _logger.LogWarning("Failed to retrieve raw device JSON: {StatusCode}", response.StatusCode);
            return null;
        });
    }

    /// <summary>
    /// GET /proxy/network/api/s/{site}/stat/device/{mac} (UniFi OS) or
    /// GET /api/s/{site}/stat/device/{mac} (standalone) - Get specific device by MAC address
    /// </summary>
    public async Task<UniFiDeviceResponse?> GetDeviceAsync(string mac, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching device {Mac} from site {Site}", mac, _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiDeviceResponse>>(
            () => _httpClient!.GetAsync(BuildApiPath($"stat/device/{mac}"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok" && response.Data.Count > 0)
        {
            return response.Data[0];
        }

        _logger.LogWarning("Device {Mac} not found", mac);
        return null;
    }

    #endregion

    #region Client Management APIs

    /// <summary>
    /// GET stat/sta - Get all connected clients
    /// </summary>
    public async Task<List<UniFiClientResponse>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching all clients from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiClientResponse>>(
            () => _httpClient!.GetAsync(BuildApiPath("stat/sta"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogInformation("Retrieved {Count} clients", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve clients or received non-ok response");
        return new List<UniFiClientResponse>();
    }

    /// <summary>
    /// GET stat/sta/{mac} - Get specific client by MAC address
    /// </summary>
    public async Task<UniFiClientResponse?> GetClientAsync(string mac, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching client {Mac} from site {Site}", mac, _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiClientResponse>>(
            () => _httpClient!.GetAsync(BuildApiPath($"stat/sta/{mac}"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok" && response.Data.Count > 0)
        {
            return response.Data[0];
        }

        _logger.LogWarning("Client {Mac} not found", mac);
        return null;
    }

    /// <summary>
    /// GET rest/user - Get all known users (includes historical clients)
    /// </summary>
    public async Task<List<UniFiClientResponse>> GetAllKnownClientsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching all known users from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiClientResponse>>(
            () => _httpClient!.GetAsync(BuildApiPath("rest/user"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogInformation("Retrieved {Count} known users", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve known users or received non-ok response");
        return new List<UniFiClientResponse>();
    }

    #endregion

    #region Firewall Management APIs

    /// <summary>
    /// GET rest/firewallrule - Get all firewall rules
    /// </summary>
    public async Task<List<UniFiFirewallRule>> GetFirewallRulesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching firewall rules from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiFirewallRule>>(
            () => _httpClient!.GetAsync(BuildApiPath("rest/firewallrule"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogInformation("Retrieved {Count} firewall rules", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve firewall rules or received non-ok response");
        return new List<UniFiFirewallRule>();
    }

    /// <summary>
    /// GET rest/firewallgroup - Get all firewall groups (address groups, port groups)
    /// </summary>
    public async Task<List<UniFiFirewallGroup>> GetFirewallGroupsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching firewall groups from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiFirewallGroup>>(
            () => _httpClient!.GetAsync(BuildApiPath("rest/firewallgroup"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogInformation("Retrieved {Count} firewall groups", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve firewall groups or received non-ok response");
        return new List<UniFiFirewallGroup>();
    }

    #endregion

    #region Network Configuration APIs

    /// <summary>
    /// GET rest/networkconf - Get all network/VLAN configurations
    /// </summary>
    public async Task<List<UniFiNetworkConfig>> GetNetworkConfigsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching network configs from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiNetworkConfig>>(
            () => _httpClient!.GetAsync(BuildApiPath("rest/networkconf"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogInformation("Retrieved {Count} network configs", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve network configs or received non-ok response");
        return new List<UniFiNetworkConfig>();
    }

    /// <summary>
    /// Get WAN configurations only (filtered from network configs)
    /// Returns networks with purpose = "wan"
    /// </summary>
    public async Task<List<UniFiNetworkConfig>> GetWanConfigsAsync(CancellationToken cancellationToken = default)
    {
        var allConfigs = await GetNetworkConfigsAsync(cancellationToken);
        var wanConfigs = allConfigs
            .Where(c => c.Purpose.Equals("wan", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _logger.LogInformation("Found {Count} WAN configurations", wanConfigs.Count);
        return wanConfigs;
    }

    /// <summary>
    /// PUT rest/networkconf/{id} - Update network configuration
    /// Used to enable/disable networks, VPNs, etc.
    /// </summary>
    public async Task<bool> UpdateNetworkConfigAsync(
        string configId,
        UniFiNetworkConfig updatedConfig,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating network config {ConfigId}", configId);

        var content = new StringContent(
            JsonSerializer.Serialize(updatedConfig),
            Encoding.UTF8,
            "application/json");

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiNetworkConfig>>(
            () => _httpClient!.PutAsync(
                BuildApiPath($"rest/networkconf/{configId}"),
                content,
                cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogInformation("Successfully updated network config {ConfigId}", configId);
            return true;
        }

        _logger.LogWarning("Failed to update network config {ConfigId}", configId);
        return false;
    }

    #endregion

    #region System Information APIs

    /// <summary>
    /// GET stat/sysinfo - Get controller system info (includes licensing fingerprint)
    /// </summary>
    public async Task<UniFiSysInfo?> GetSystemInfoAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching system info from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiSysInfoResponse>(
            () => _httpClient!.GetAsync(BuildApiPath("stat/sysinfo"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok" && response.Data.Count > 0)
        {
            var sysInfo = response.Data[0];
            _logger.LogInformation("Retrieved system info - Controller: {Name} v{Version}",
                sysInfo.Name, sysInfo.Version);

            if (!string.IsNullOrEmpty(sysInfo.AnonymousControllerId))
            {
                _logger.LogDebug("Controller fingerprint: {ControllerId}", sysInfo.AnonymousControllerId);
            }

            return sysInfo;
        }

        _logger.LogWarning("Failed to retrieve system info or received non-ok response");
        return null;
    }

    /// <summary>
    /// GET /api/self - Get information about the current logged-in user
    /// Note: This endpoint doesn't use the /proxy/network prefix even on UniFi OS
    /// </summary>
    public async Task<JsonDocument?> GetSelfInfoAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching self info");

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync($"{_controllerUrl}/api/self", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonDocument.Parse(json);
            }

            return null;
        });
    }

    /// <summary>
    /// GET stat/health - Get site health information
    /// </summary>
    public async Task<JsonDocument?> GetSiteHealthAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching site health for {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(BuildApiPath("stat/health"), cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonDocument.Parse(json);
            }

            return null;
        });
    }

    #endregion

    #region Traffic Management APIs

    /// <summary>
    /// GET /proxy/network/v2/api/site/{site}/trafficroutes - Get traffic routes
    /// This is a newer UniFi Network Application (v2) endpoint
    /// </summary>
    public async Task<JsonDocument?> GetTrafficRoutesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching traffic routes for site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(
                $"{_controllerUrl}/proxy/network/v2/api/site/{_site}/trafficroutes",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonDocument.Parse(json);
            }

            return null;
        });
    }

    /// <summary>
    /// PUT /proxy/network/v2/api/site/{site}/trafficroutes/{id} - Update traffic route
    /// </summary>
    public async Task<bool> UpdateTrafficRouteAsync(
        string routeId,
        JsonDocument route,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating traffic route {RouteId}", routeId);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return false;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var content = new StringContent(
                route.RootElement.GetRawText(),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient!.PutAsync(
                $"{_controllerUrl}/proxy/network/v2/api/site/{_site}/trafficroutes/{routeId}",
                content,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully updated traffic route {RouteId}", routeId);
                return true;
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Failed to update traffic route {RouteId}: {Error}", routeId, errorBody);
            return false;
        });
    }

    #endregion

    #region Statistics APIs

    /// <summary>
    /// GET stat/report/hourly.site - Get hourly site statistics
    /// </summary>
    public async Task<JsonDocument?> GetHourlySiteStatsAsync(
        DateTime? start = null,
        DateTime? end = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = start ?? DateTime.UtcNow.AddHours(-24);
        var endTime = end ?? DateTime.UtcNow;

        var startMs = new DateTimeOffset(startTime).ToUnixTimeMilliseconds();
        var endMs = new DateTimeOffset(endTime).ToUnixTimeMilliseconds();

        _logger.LogDebug("Fetching hourly site stats from {Start} to {End}", startTime, endTime);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        var url = $"{BuildApiPath("stat/report/hourly.site")}?start={startMs}&end={endMs}";

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonDocument.Parse(json);
            }

            return null;
        });
    }

    #endregion

    #region Site Management

    /// <summary>
    /// GET api/self/sites - Get all sites accessible to the current user
    /// Note: On UniFi OS this also needs the /proxy/network prefix
    /// </summary>
    public async Task<JsonDocument?> GetSitesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching all sites");

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        // Build URL - self/sites endpoint also uses the proxy path on UniFi OS
        var url = _isUniFiOs
            ? $"{_controllerUrl}/proxy/network/api/self/sites"
            : $"{_controllerUrl}/api/self/sites";

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonDocument.Parse(json);
            }

            return null;
        });
    }

    #endregion

    /// <summary>
    /// Logout from the controller (optional, as cookies typically expire)
    /// </summary>
    public async Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (!_isAuthenticated)
            return true;

        try
        {
            _logger.LogDebug("Logging out from UniFi controller");

            var response = await _httpClient!.PostAsync(
                $"{_controllerUrl}/api/logout",
                null,
                cancellationToken);

            _isAuthenticated = false;
            _csrfToken = null;

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully logged out");
                return true;
            }

            _logger.LogWarning("Logout returned status {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during logout");
            return false;
        }
    }

    public void Dispose()
    {
        _authLock?.Dispose();
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
