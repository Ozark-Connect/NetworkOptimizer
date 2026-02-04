using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Models;
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
    private readonly bool _ignoreSSLErrors;
    private HttpClient? _httpClient;
    private CookieContainer? _cookieContainer;
    private string? _csrfToken;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private bool _isAuthenticated = false;
    private bool _isUniFiOs = false; // True for UDM/UCG, false for standalone controller
    private bool _pathDetected = false;
    private bool _useStandaloneLogin = false; // True for standalone Network controllers (uses /api/login)
    private string? _lastLoginError;
    private string? _lastApiError;
    private string? _lastApiErrorCode;

    /// <summary>
    /// Gets the last login error message (e.g., rate limiting, SSL errors)
    /// </summary>
    public string? LastLoginError => _lastLoginError;

    /// <summary>
    /// Gets the last API error message from a site-specific API call
    /// </summary>
    public string? LastApiError => _lastApiError;

    /// <summary>
    /// Gets the last API error code (e.g., "api.err.NoSiteContext")
    /// </summary>
    public string? LastApiErrorCode => _lastApiErrorCode;

    public UniFiApiClient(
        ILogger<UniFiApiClient> logger,
        string controllerHost,
        string username,
        string password,
        string site = "default",
        bool ignoreSSLErrors = true)
    {
        _logger = logger;
        _controllerUrl = controllerHost.StartsWith("https://") ? controllerHost : $"https://{controllerHost}";
        _username = username;
        _password = password;
        _site = site;
        _ignoreSSLErrors = ignoreSSLErrors;

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
            UseCookies = true
        };

        // UniFi controllers typically use self-signed certificates.
        // This setting allows bypassing SSL validation when enabled (default: true).
        if (_ignoreSSLErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "NetworkOptimizer.UniFi/1.0");
    }

    /// <summary>
    /// Detects whether this is a UniFi OS controller or standalone Network controller
    /// by checking the login page endpoints.
    /// - UniFi OS (UDM/UCG): GET /login returns 200 → use /api/auth/login
    /// - Standalone Network: GET /login returns 404, /manage/account/login exists → use /api/login
    /// </summary>
    private async Task DetectLoginTypeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Detecting login type (UniFi OS vs standalone Network controller)...");

        try
        {
            // Try GET /login - UniFi OS returns 200, standalone returns 404
            var response = await _httpClient!.GetAsync($"{_controllerUrl}/login", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                // UniFi OS - use /api/auth/login
                _useStandaloneLogin = false;
                _logger.LogDebug("Detected UniFi OS login page - will use /api/auth/login");
                return;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Check for standalone Network controller login page
                _logger.LogDebug("GET /login returned 404, checking for standalone Network controller...");

                var manageResponse = await _httpClient!.GetAsync(
                    $"{_controllerUrl}/manage/account/login",
                    cancellationToken);

                if (manageResponse.IsSuccessStatusCode)
                {
                    // Standalone Network controller - use /api/login
                    _useStandaloneLogin = true;
                    _logger.LogInformation("Detected standalone UniFi Network controller - will use /api/login");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Login type detection failed: {Message}", ex.Message);
        }

        // Default to UniFi OS (most common modern scenario)
        _useStandaloneLogin = false;
        _logger.LogDebug("Defaulting to UniFi OS login endpoint");
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

            // Detect which login endpoint to use
            await DetectLoginTypeAsync(cancellationToken);

            var loginRequest = new UniFiLoginRequest
            {
                Username = _username,
                Password = _password,
                Remember = false,
                Strict = true
            };

            // Use appropriate login endpoint based on controller type
            var loginUrl = _useStandaloneLogin
                ? $"{_controllerUrl}/api/login"
                : $"{_controllerUrl}/api/auth/login";

            _logger.LogDebug("Using login endpoint: {LoginUrl}", loginUrl);

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

                // Parse error response for user-friendly message
                _lastLoginError = ParseLoginError(response.StatusCode, errorBody);
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
            _lastLoginError = ParseExceptionError(ex);
            return false;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// Parses login error response from the controller
    /// </summary>
    private string ParseLoginError(HttpStatusCode statusCode, string errorBody)
    {
        try
        {
            // Try to parse JSON error response
            using var doc = JsonDocument.Parse(errorBody);
            var root = doc.RootElement;

            // Check for message field (UniFi error format)
            if (root.TryGetProperty("message", out var messageElement))
            {
                var message = messageElement.GetString();
                if (!string.IsNullOrEmpty(message))
                {
                    // Add context for rate limiting
                    if (statusCode == HttpStatusCode.TooManyRequests ||
                        message.Contains("limit", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"Rate limited: {message}. Wait a few minutes before trying again.";
                    }
                    return message;
                }
            }

            // Check for error field
            if (root.TryGetProperty("error", out var errorElement))
            {
                var error = errorElement.GetString();
                if (!string.IsNullOrEmpty(error))
                    return error;
            }
        }
        catch
        {
            // JSON parsing failed, use status code
        }

        // Fallback based on status code
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "Invalid username or password",
            HttpStatusCode.Forbidden => "Access denied. Check user permissions.",
            HttpStatusCode.TooManyRequests => "Too many login attempts against UniFi Console. Wait a few minutes before trying again.",
            HttpStatusCode.ServiceUnavailable => "Console is unavailable. Check if it's running.",
            _ => $"Authentication failed (HTTP {(int)statusCode})"
        };
    }

    /// <summary>
    /// Parses exception for user-friendly error message
    /// </summary>
    private string ParseExceptionError(Exception ex)
    {
        // Check for SSL/TLS certificate errors
        if (ex is HttpRequestException httpEx)
        {
            var message = ex.Message;
            var innerMessage = ex.InnerException?.Message ?? "";

            // SSL certificate validation failure
            if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                innerMessage.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
                innerMessage.Contains("RemoteCertificate", StringComparison.OrdinalIgnoreCase))
            {
                // Provide specific guidance based on certificate error type
                if (innerMessage.Contains("RemoteCertificateNameMismatch"))
                {
                    return "SSL certificate error: The certificate doesn't match the hostname. Enable 'Ignore SSL Errors' in settings, or use the correct hostname.";
                }
                if (innerMessage.Contains("RemoteCertificateChainErrors"))
                {
                    return "SSL certificate error: Self-signed or untrusted certificate. Enable 'Ignore SSL Errors' in settings.";
                }
                return "SSL certificate error: Unable to establish secure connection. Enable 'Ignore SSL Errors' in settings.";
            }

            // Connection refused
            if (message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            {
                return "Connection refused. Check if the controller is running and the URL is correct.";
            }

            // Host not found
            if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("host is known", StringComparison.OrdinalIgnoreCase))
            {
                return "Host not found. Check the controller URL.";
            }

            // Timeout
            if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return "Connection timed out. Check network connectivity and firewall settings.";
            }
        }

        // Generic fallback
        return ex.Message;
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
        string url;
        if (_isUniFiOs)
        {
            url = $"{_controllerUrl}/proxy/network/api/s/{_site}/{endpoint}";
        }
        else
        {
            // For standalone controllers
            url = $"{_controllerUrl}/api/s/{_site}/{endpoint}";
        }
        _logger.LogDebug("BuildApiPath: _isUniFiOs={IsUniFiOs}, endpoint={Endpoint}, url={Url}", _isUniFiOs, endpoint, url);
        return url;
    }

    /// <summary>
    /// Builds the correct V2 API path based on whether this is UniFi OS or standalone controller
    /// </summary>
    private string BuildV2ApiPath(string endpoint)
    {
        // For UniFi OS (UDM/UCG), V2 APIs are proxied through /proxy/network
        string url;
        if (_isUniFiOs)
        {
            url = $"{_controllerUrl}/proxy/network/v2/api/{endpoint}";
        }
        else
        {
            // For standalone controllers
            url = $"{_controllerUrl}/v2/api/{endpoint}";
        }
        _logger.LogDebug("BuildV2ApiPath: _isUniFiOs={IsUniFiOs}, endpoint={Endpoint}, url={Url}", _isUniFiOs, endpoint, url);
        return url;
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
        var unifiOsProbeUrl = $"{_controllerUrl}/proxy/network/api/s/{_site}/stat/sysinfo";
        try
        {
            _logger.LogDebug("Probing UniFi OS path: {Url}", unifiOsProbeUrl);
            var response = await _httpClient!.GetAsync(unifiOsProbeUrl, cancellationToken);
            _logger.LogDebug("UniFi OS probe response: {StatusCode} ({StatusCodeInt})", response.StatusCode, (int)response.StatusCode);

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
        var standaloneProbeUrl = $"{_controllerUrl}/api/s/{_site}/stat/sysinfo";
        try
        {
            _logger.LogDebug("Probing standalone path: {Url}", standaloneProbeUrl);
            var response = await _httpClient!.GetAsync(standaloneProbeUrl, cancellationToken);
            _logger.LogDebug("Standalone probe response: {StatusCode} ({StatusCodeInt})", response.StatusCode, (int)response.StatusCode);

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
    /// Gets whether this is a standalone Network controller (uses /api/login instead of /api/auth/login)
    /// </summary>
    public bool IsStandaloneNetworkController => _useStandaloneLogin;

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

    /// <summary>
    /// GET v2/api/site/{site}/device - Get all device types including Protect devices
    /// This v2 API returns network_devices, protect_devices, access_devices, etc.
    /// Only available on UniFi OS controllers (UDM, UCG, etc.)
    /// </summary>
    public async Task<UniFiAllDevicesResponse?> GetAllDevicesV2Async(CancellationToken cancellationToken = default)
    {
        if (!_isUniFiOs)
        {
            _logger.LogDebug("V2 device API not available on standalone controllers");
            return null;
        }

        _logger.LogDebug("Fetching all device types (v2 API) from site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        var url = BuildV2ApiPath($"site/{_site}/device");

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<UniFiAllDevicesResponse>(cancellationToken: cancellationToken);
                if (result != null)
                {
                    var protectCount = result.ProtectDevices?.Count ?? 0;
                    var networkCount = result.NetworkDevices?.Count ?? 0;
                    _logger.LogInformation("Retrieved {NetworkCount} network devices and {ProtectCount} Protect devices (v2 API)",
                        networkCount, protectCount);
                }
                return result;
            }

            _logger.LogWarning("Failed to retrieve devices from v2 API: {StatusCode}", response.StatusCode);
            return null;
        });
    }

    /// <summary>
    /// Get UniFi Protect devices that require Security VLAN placement
    /// Returns a collection of cameras, doorbells, NVRs, and AI processors with their names
    /// </summary>
    public async Task<ProtectCameraCollection> GetProtectCamerasAsync(CancellationToken cancellationToken = default)
    {
        var result = new ProtectCameraCollection();

        var allDevices = await GetAllDevicesV2Async(cancellationToken);
        if (allDevices?.ProtectDevices == null)
        {
            return result;
        }

        foreach (var device in allDevices.ProtectDevices)
        {
            if (device.RequiresSecurityVlan)
            {
                var name = !string.IsNullOrEmpty(device.Name) ? device.Name : device.Model ?? "Protect Device";
                result.Add(device.Mac, name, device.ConnectionNetworkId);

                var deviceType = device.IsCamera ? "camera" :
                                 device.IsDoorbell ? "doorbell" :
                                 device.IsNvr ? "NVR" :
                                 device.IsVideoProcessor ? "AI processor" : "device";
                _logger.LogDebug("Found Protect {DeviceType}: {Name} ({Model}) - MAC: {Mac}, NetworkId: {NetworkId}",
                    deviceType, device.Name, device.Model, device.Mac, device.ConnectionNetworkId ?? "null");
            }
        }

        _logger.LogInformation("Found {Count} Protect devices requiring Security VLAN", result.Count);
        return result;
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

    /// <summary>
    /// GET v2/api/site/{site}/clients/active - Get currently active clients with full details
    /// This endpoint returns IP addresses even for UX/UX7 connected clients (unlike stat/sta)
    /// </summary>
    public async Task<List<UniFiClientDetailResponse>> GetActiveClientsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching active clients from site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return new List<UniFiClientDetailResponse>();
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var url = BuildV2ApiPath($"site/{_site}/clients/active");
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var clients = await response.Content.ReadFromJsonAsync<List<UniFiClientDetailResponse>>(
                    cancellationToken: cancellationToken);

                _logger.LogDebug("Retrieved {Count} active clients", clients?.Count ?? 0);
                return clients ?? new List<UniFiClientDetailResponse>();
            }

            _logger.LogWarning("Failed to retrieve active clients: {StatusCode}", response.StatusCode);
            return new List<UniFiClientDetailResponse>();
        });
    }

    /// <summary>
    /// GET v2/api/site/{site}/clients/history - Get client history (includes offline devices)
    /// </summary>
    /// <param name="withinHours">How far back to look (default 720 = 30 days)</param>
    public async Task<List<UniFiClientDetailResponse>> GetClientHistoryAsync(
        int withinHours = 720,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching client history (within {Hours} hours) from site {Site}", withinHours, _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return new List<UniFiClientDetailResponse>();
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var url = BuildV2ApiPath($"site/{_site}/clients/history?withinHours={withinHours}");
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var clients = await response.Content.ReadFromJsonAsync<List<UniFiClientDetailResponse>>(
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Retrieved {Count} historical clients", clients?.Count ?? 0);
                return clients ?? new List<UniFiClientDetailResponse>();
            }

            _logger.LogWarning("Failed to retrieve client history: {StatusCode}", response.StatusCode);
            return new List<UniFiClientDetailResponse>();
        });
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
    /// GET rest/firewallrule - Get all firewall rules as raw JSON (legacy v1 API).
    /// Use this for parsing rules through FirewallRuleParser when the v2 policies API is unavailable.
    /// </summary>
    public async Task<JsonDocument?> GetLegacyFirewallRulesRawAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching legacy firewall rules (raw) from site {Site}", _site);

        try
        {
            var response = await _httpClient!.GetAsync(BuildApiPath("rest/firewallrule"), cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonDocument.Parse(json);

            // Check for successful API response
            if (doc.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("rc", out var rc) &&
                rc.GetString() == "ok" &&
                doc.RootElement.TryGetProperty("data", out var data))
            {
                var count = data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : 0;
                _logger.LogInformation("Retrieved {Count} legacy firewall rules (raw)", count);
                return doc;
            }

            _logger.LogWarning("Legacy firewall rules response did not have expected format");
            doc.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch legacy firewall rules");
            return null;
        }
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

    /// <summary>
    /// GET stat/portforward - Get all port forwarding rules (UPnP and static)
    /// Returns both dynamic UPnP mappings and configured static port forwards
    /// </summary>
    public async Task<List<UniFiPortForwardRule>> GetPortForwardRulesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching port forwarding rules from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiPortForwardRule>>(
            () => _httpClient!.GetAsync(BuildApiPath("stat/portforward"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            var upnpCount = response.Data.Count(r => r.IsUpnp == 1);
            var staticCount = response.Data.Count - upnpCount;
            _logger.LogInformation("Retrieved {Count} port forwarding rules ({UpnpCount} UPnP, {StaticCount} static)",
                response.Data.Count, upnpCount, staticCount);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve port forwarding rules or received non-ok response");
        return new List<UniFiPortForwardRule>();
    }

    /// <summary>
    /// GET v2/api/site/{site}/firewall/zone - Get all firewall zones.
    /// Returns the predefined zones (internal, external, gateway, vpn, hotspot, dmz)
    /// and which networks are assigned to each zone.
    /// </summary>
    public async Task<List<UniFiFirewallZone>> GetFirewallZonesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching firewall zones from site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            _logger.LogWarning("Failed to authenticate when fetching firewall zones");
            return [];
        }

        try
        {
            var url = BuildV2ApiPath($"site/{_site}/firewall/zone");
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to retrieve firewall zones: {StatusCode}", response.StatusCode);
                return [];
            }

            var zones = await response.Content.ReadFromJsonAsync<List<UniFiFirewallZone>>(cancellationToken: cancellationToken);

            if (zones != null)
            {
                _logger.LogInformation("Retrieved {Count} firewall zones", zones.Count);
                return zones;
            }

            _logger.LogWarning("Failed to deserialize firewall zones response");
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch firewall zones");
            return [];
        }
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
    /// GET rest/portconf - Get all port profiles.
    /// Port profiles define configuration templates that can be applied to switch ports.
    /// When a port has a portconf_id, its settings (forward mode, isolation, etc.) come from the profile.
    /// </summary>
    public async Task<List<UniFiPortProfile>> GetPortProfilesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching port profiles from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiPortProfile>>(
            () => _httpClient!.GetAsync(BuildApiPath("rest/portconf"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogInformation("Retrieved {Count} port profiles", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve port profiles or received non-ok response");
        return new List<UniFiPortProfile>();
    }

    /// <summary>
    /// GET rest/wlanconf - Get all WLAN (WiFi network) configurations.
    /// Returns full WLAN settings including mlo_enabled, security settings, etc.
    /// </summary>
    public async Task<List<UniFiWlanConfig>> GetWlanConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching WLAN configurations from site {Site}", _site);

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiWlanConfig>>(
            () => _httpClient!.GetAsync(BuildApiPath("rest/wlanconf"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogDebug("Retrieved {Count} WLAN configurations", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve WLAN configurations or received non-ok response");
        return new List<UniFiWlanConfig>();
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
    /// GET v2/api/site/{site}/trafficroutes - Get traffic routes
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
                BuildV2ApiPath($"site/{_site}/trafficroutes"),
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
    /// PUT v2/api/site/{site}/trafficroutes/{id} - Update traffic route
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
                BuildV2ApiPath($"site/{_site}/trafficroutes/{routeId}"),
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

    #region Settings APIs

    /// <summary>
    /// GET rest/setting - Get all site settings (includes DoH, DNS, etc.)
    /// </summary>
    public async Task<JsonDocument?> GetSettingsRawAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching settings from site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(BuildApiPath("rest/setting"), cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Retrieved settings ({Length} bytes)", json.Length);
                return JsonDocument.Parse(json);
            }

            _logger.LogWarning("Failed to retrieve settings: {StatusCode}", response.StatusCode);
            return null;
        });
    }

    /// <summary>
    /// Check if UPnP is enabled in the USG settings
    /// </summary>
    public async Task<bool> GetUpnpEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var settings = await GetSettingsRawAsync(cancellationToken);
            if (settings == null) return true; // Assume enabled if we can't fetch

            if (settings.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("key", out var key) && key.GetString() == "usg")
                    {
                        if (item.TryGetProperty("upnp_enabled", out var upnpEnabled))
                        {
                            return upnpEnabled.GetBoolean();
                        }
                    }
                }
            }

            return true; // Assume enabled if not found
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check UPnP enabled status");
            return true; // Assume enabled on error
        }
    }

    /// <summary>
    /// GET v2/api/site/{site}/firewall-policies - Get firewall policies (new v2 API)
    /// This endpoint provides detailed firewall policy configuration including DNS blocking rules
    /// </summary>
    public async Task<JsonDocument?> GetFirewallPoliciesRawAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching firewall policies from site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var url = BuildV2ApiPath($"site/{_site}/firewall-policies");
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Retrieved firewall policies ({Length} bytes)", json.Length);
                return JsonDocument.Parse(json);
            }

            _logger.LogWarning("Failed to retrieve firewall policies: {StatusCode}", response.StatusCode);
            return null;
        });
    }

    /// <summary>
    /// GET v2/api/site/{site}/nat - Get NAT rules (DNAT/SNAT)
    /// This endpoint provides NAT rule configuration for DNS redirection detection
    /// </summary>
    public async Task<JsonDocument?> GetNatRulesRawAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching NAT rules from site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var url = BuildV2ApiPath($"site/{_site}/nat");
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Retrieved NAT rules ({Length} bytes)", json.Length);
                return JsonDocument.Parse(json);
            }

            _logger.LogWarning("Failed to retrieve NAT rules: {StatusCode}", response.StatusCode);
            return null;
        });
    }

    /// <summary>
    /// GET v2/api/site/{site}/firewall-rules/combined-traffic-firewall-rules?originType=all
    /// Returns combined traffic/firewall rules including app-based rules.
    /// This API is used to get app-based DNS blocking rules that use application IDs
    /// instead of port numbers.
    /// </summary>
    public async Task<JsonDocument?> GetCombinedTrafficFirewallRulesRawAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching combined traffic firewall rules from site {Site}", _site);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var url = BuildV2ApiPath($"site/{_site}/firewall-rules/combined-traffic-firewall-rules?originType=all");
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Retrieved combined traffic firewall rules ({Length} bytes)", json.Length);
                return JsonDocument.Parse(json);
            }

            _logger.LogWarning("Failed to retrieve combined traffic firewall rules: {StatusCode}", response.StatusCode);
            return null;
        });
    }

    #endregion

    #region Fingerprint Database APIs

    /// <summary>
    /// GET v2/api/fingerprint_devices/{index} - Get fingerprint database
    /// The database is split across multiple indices (0-n)
    /// </summary>
    public async Task<UniFiFingerprintDatabase?> GetFingerprintDatabaseAsync(
        int index = 0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching fingerprint database index {Index}", index);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return null;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var url = BuildV2ApiPath($"fingerprint_devices/{index}");
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<UniFiFingerprintDatabase>(
                    cancellationToken: cancellationToken);
            }

            _logger.LogDebug("Fingerprint database index {Index} returned {StatusCode}",
                index, response.StatusCode);
            return null;
        });
    }

    /// <summary>
    /// Get the complete fingerprint database by fetching all indices
    /// </summary>
    public async Task<UniFiFingerprintDatabase> GetCompleteFingerprintDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        var combined = new UniFiFingerprintDatabase();
        var maxIndices = 15; // UniFi typically has indices 0-10+
        var indicesFetched = 0;

        for (int i = 0; i <= maxIndices; i++)
        {
            var db = await GetFingerprintDatabaseAsync(i, cancellationToken);
            if (db == null)
            {
                _logger.LogDebug("Fingerprint database: fetched {Count} indices (0-{Last})",
                    indicesFetched, i - 1);
                break;
            }

            combined.Merge(db);
            indicesFetched++;
            _logger.LogDebug("Merged fingerprint index {Index} - Total devices: {Count}",
                i, combined.DevIds.Count);
        }

        _logger.LogInformation("Loaded fingerprint database: {DevTypes} device types, {Vendors} vendors, {Devices} devices",
            combined.DevTypeIds.Count, combined.VendorIds.Count, combined.DevIds.Count);

        return combined;
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

    /// <summary>
    /// Validates that the configured site ID exists on this controller.
    /// Call this after login to verify the site is accessible.
    /// </summary>
    /// <returns>Tuple of (success, error message if failed)</returns>
    public async Task<(bool Success, string? Error)> ValidateSiteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Validating site '{Site}' on controller", _site);

        // Clear any previous API errors
        _lastApiError = null;
        _lastApiErrorCode = null;

        try
        {
            // Make a minimal site-specific call to verify the site exists
            var url = BuildApiPath("stat/sysinfo");
            var response = await _httpClient!.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse the response to check for API-level errors
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("meta", out var meta))
            {
                var rc = meta.TryGetProperty("rc", out var rcProp) ? rcProp.GetString() : null;
                var msg = meta.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() : null;

                if (rc != "ok")
                {
                    _lastApiError = msg;
                    _lastApiErrorCode = msg;

                    _logger.LogWarning("Site validation failed: {Error}", msg);

                    // Provide user-friendly error messages for known error codes
                    if (msg == "api.err.NoSiteContext")
                    {
                        var error = $"Invalid Site ID: The site '{_site}' does not exist on this controller.";
                        return (false, error);
                    }

                    return (false, $"API error: {msg}");
                }
            }

            _logger.LogDebug("Site '{Site}' validated successfully", _site);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during site validation");
            return (false, $"Failed to validate site: {ex.Message}");
        }
    }

    #region Wi-Fi Optimizer APIs

    /// <summary>
    /// POST stat/report/{granularity}.site - Get site-wide Wi-Fi metrics time series
    /// </summary>
    /// <param name="granularity">Report granularity: 5minutes, hourly, daily</param>
    /// <param name="startMs">Start time in Unix milliseconds</param>
    /// <param name="endMs">End time in Unix milliseconds</param>
    /// <param name="attrs">Attributes to fetch (e.g., ap-ng-cu_total, ap-na-tx_retries)</param>
    public async Task<JsonElement> PostSiteReportAsync(
        string granularity,
        long startMs,
        long endMs,
        string[] attrs,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching {Granularity} site report with {AttrCount} attributes", granularity, attrs.Length);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return default;
        }

        var url = BuildApiPath($"stat/report/{granularity}.site");
        var payload = new
        {
            attrs,
            start = startMs,
            end = endMs
        };

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient!.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    return data.Clone();
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Site report request failed: {StatusCode} - {Error}",
                    response.StatusCode, error);
            }

            return default;
        });
    }

    /// <summary>
    /// POST stat/report/{granularity}.ap - Get per-AP Wi-Fi metrics time series
    /// </summary>
    /// <param name="granularity">Report granularity: 5minutes, hourly, daily</param>
    /// <param name="apMacs">AP MAC addresses to filter by</param>
    /// <param name="startMs">Start time in Unix milliseconds</param>
    /// <param name="endMs">End time in Unix milliseconds</param>
    /// <param name="attrs">Attributes to fetch (e.g., ng-cu_total, na-cu_total - note: no 'ap-' prefix for .ap endpoint)</param>
    public async Task<JsonElement> PostApReportAsync(
        string granularity,
        string[] apMacs,
        long startMs,
        long endMs,
        string[] attrs,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching {Granularity} AP report for {ApCount} APs", granularity, apMacs.Length);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return default;
        }

        var url = BuildApiPath($"stat/report/{granularity}.ap");
        var payload = new
        {
            attrs,
            macs = apMacs,
            start = startMs,
            end = endMs
        };

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient!.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    return data.Clone();
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("AP report request failed: {StatusCode} - {Error}",
                    response.StatusCode, error);
            }

            return default;
        });
    }

    /// <summary>
    /// POST stat/report/{granularity}.user - Get per-client Wi-Fi metrics time series
    /// </summary>
    /// <param name="granularity">Report granularity: 5minutes, hourly, daily</param>
    /// <param name="clientMac">Client MAC address</param>
    /// <param name="startMs">Start time in Unix milliseconds</param>
    /// <param name="endMs">End time in Unix milliseconds</param>
    /// <param name="attrs">Attributes to fetch (e.g., signal, tx_retries)</param>
    public async Task<JsonElement> PostUserReportAsync(
        string granularity,
        string clientMac,
        long startMs,
        long endMs,
        string[] attrs,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching {Granularity} user report for {ClientMac}", granularity, clientMac);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return default;
        }

        var url = BuildApiPath($"stat/report/{granularity}.user");
        var payload = new
        {
            attrs,
            macs = new[] { clientMac },
            oid = clientMac,  // Required by UniFi API
            start = startMs,
            end = endMs
        };

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient!.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    return data.Clone();
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("User report request failed: {StatusCode} - {Error}",
                    response.StatusCode, error);
            }

            return default;
        });
    }

    /// <summary>
    /// GET v2/api/site/{site}/wlan/enriched-configuration - Get WLAN configurations with stats
    /// </summary>
    public async Task<JsonElement> GetWlanEnrichedConfigurationAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching WLAN enriched configuration");

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return default;
        }

        var url = BuildV2ApiPath($"site/{_site}/wlan/enriched-configuration");

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("WLAN enriched config request failed: {StatusCode} - {Error}",
                    response.StatusCode, error);
            }

            return default;
        });
    }

    /// <summary>
    /// POST v2/api/site/{site}/wifi-connectivity/roaming/topology - Get roaming topology and statistics
    /// </summary>
    public async Task<JsonElement> GetRoamingTopologyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching roaming topology");

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return default;
        }

        var url = BuildV2ApiPath($"site/{_site}/wifi-connectivity/roaming/topology");

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            // This endpoint requires POST with empty body
            var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient!.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Roaming topology request failed: {StatusCode} - {Error}",
                    response.StatusCode, error);
            }

            return default;
        });
    }

    /// <summary>
    /// GET v2/api/site/{site}/system-log/client-connection/{mac} - Get client connection events (connects, disconnects, roams)
    /// </summary>
    /// <param name="clientMac">Client MAC address</param>
    /// <param name="limit">Maximum number of events to return (default 200)</param>
    public async Task<JsonElement> GetClientConnectionEventsAsync(
        string clientMac,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching client connection events for {ClientMac}", clientMac);

        if (!await EnsureAuthenticatedAsync(cancellationToken))
        {
            return default;
        }

        var url = BuildV2ApiPath($"site/{_site}/system-log/client-connection/{clientMac}?mac={clientMac}&separateConnectionSignalParam=false&limit={limit}");

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var response = await _httpClient!.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Client connection events request failed: {StatusCode} - {Error}",
                    response.StatusCode, error);
            }

            return default;
        });
    }

    /// <summary>
    /// GET /api/s/{site}/stat/rogueap - Get neighboring Wi-Fi networks detected by APs
    /// </summary>
    /// <param name="startTime">Start time for filtering (optional, defaults to 1 day ago). UniFi UI uses 30m, 1h, 1D, 1W, 1M ranges.</param>
    /// <param name="endTime">End time for filtering (optional, defaults to now)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<List<UniFiRogueApResponse>> GetRogueApsAsync(
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching rogue/neighboring APs");

        var end = endTime ?? DateTimeOffset.UtcNow;
        var start = startTime ?? end.AddDays(-1);

        var startSeconds = start.ToUnixTimeSeconds();
        var endSeconds = end.ToUnixTimeSeconds();

        var response = await ExecuteApiCallAsync<UniFiApiResponse<UniFiRogueApResponse>>(
            () => _httpClient!.GetAsync(BuildApiPath($"stat/rogueap?start={startSeconds}&end={endSeconds}"), cancellationToken),
            cancellationToken);

        if (response?.Meta.Rc == "ok")
        {
            _logger.LogDebug("Found {Count} rogue/neighboring APs", response.Data.Count);
            return response.Data;
        }

        _logger.LogWarning("Failed to retrieve rogue APs or received non-ok response");
        return new List<UniFiRogueApResponse>();
    }

    #endregion

    public void Dispose()
    {
        _authLock?.Dispose();
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}
