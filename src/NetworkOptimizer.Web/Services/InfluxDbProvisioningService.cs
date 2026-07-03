using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Talks to InfluxDB 2.x management endpoints to provision NO's tenancy:
/// org, buckets, and a scoped read+write token. The admin token the user supplies is held
/// in-memory ONLY for the duration of the calling request — this service never persists
/// it. The output is the scoped token that NO will use day-to-day for writes; that token
/// is what callers save via MonitoringSettings.
///
/// All probe + provisioning calls return rich result records rather than raising — wizard
/// UIs need to render specific error states (URL unreachable vs. bad token vs. lacking
/// permissions) and a thrown exception would force the UI to parse messages.
/// </summary>
public class InfluxDbProvisioningService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InfluxDbProvisioningService> _logger;

    public InfluxDbProvisioningService(
        IHttpClientFactory httpClientFactory,
        ILogger<InfluxDbProvisioningService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Try a curated list of likely InfluxDB URLs. First reachable wins. The wizard uses
    /// this on entry so a fresh user with a stock localhost InfluxDB sees "found it" without
    /// typing anything.
    /// </summary>
    public async Task<UrlProbeResult> AutoDetectUrlAsync(CancellationToken ct = default)
    {
        var candidates = new List<string>
        {
            "http://localhost:8086",
            "http://host.docker.internal:8086"
        };

        // Include the machine's own hostname for environments where InfluxDB is on the same
        // box but localhost binding differs from external binding.
        try
        {
            var host = Dns.GetHostName();
            if (!string.IsNullOrEmpty(host) && !candidates.Contains($"http://{host}:8086"))
                candidates.Add($"http://{host}:8086");
        }
        catch { /* hostname lookup not critical */ }

        foreach (var url in candidates)
        {
            var probe = await ProbeAsync(url, ct);
            if (probe.Reachable)
            {
                _logger.LogInformation("Auto-detected InfluxDB at {Url}", url);
                return probe with { CandidatesTried = candidates };
            }
        }

        return new UrlProbeResult
        {
            Url = candidates[0],
            Reachable = false,
            CandidatesTried = candidates,
            Error = "No InfluxDB reachable at the common URLs"
        };
    }

    /// <summary>Probe a single URL: HTTP GET /ping (no auth). Quick to time out.</summary>
    public async Task<UrlProbeResult> ProbeAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(3);
            var request = new HttpRequestMessage(HttpMethod.Get, NormalizeUrl(url) + "/ping");
            using var response = await http.SendAsync(request, ct);
            // InfluxDB /ping returns 204 No Content with an X-Influxdb-Version header.
            response.Headers.TryGetValues("X-Influxdb-Version", out var versionHeaders);
            var version = versionHeaders?.FirstOrDefault();
            return new UrlProbeResult
            {
                Url = url,
                Reachable = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent,
                Version = version,
                Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}"
            };
        }
        catch (Exception ex)
        {
            return new UrlProbeResult { Url = url, Reachable = false, Error = ex.Message };
        }
    }

    /// <summary>Validate an admin API token by hitting /api/v2/me. Returns the authenticated user details.</summary>
    public async Task<TokenValidationResult> ValidateAdminTokenAsync(string url, string adminToken, CancellationToken ct = default)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var request = new HttpRequestMessage(HttpMethod.Get, NormalizeUrl(url) + "/api/v2/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", adminToken);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new TokenValidationResult { Valid = false, Error = "Token rejected (401). Make sure it's an admin / all-access token." };
            if (!response.IsSuccessStatusCode)
                return new TokenValidationResult { Valid = false, Error = $"HTTP {(int)response.StatusCode}" };
            var body = await response.Content.ReadAsStringAsync(ct);
            var me = JsonSerializer.Deserialize<InfluxUser>(body, SerializerOptions);
            return new TokenValidationResult { Valid = true, UserName = me?.Name };
        }
        catch (Exception ex)
        {
            return new TokenValidationResult { Valid = false, Error = ex.Message };
        }
    }

    /// <summary>List the orgs the admin token can see. Caller renders these for the user to pick.</summary>
    public async Task<IReadOnlyList<InfluxOrg>> ListOrgsAsync(string url, string adminToken, CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, NormalizeUrl(url) + "/api/v2/orgs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", adminToken);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var list = JsonSerializer.Deserialize<InfluxOrgList>(body, SerializerOptions);
        return list?.Orgs ?? new List<InfluxOrg>();
    }

    /// <summary>Create an org. Used when the user picks 'create new' (the recommended default).</summary>
    public async Task<InfluxOrg> CreateOrgAsync(string url, string adminToken, string orgName, CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl(url) + "/api/v2/orgs")
        {
            Content = JsonContent(new { name = orgName })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", adminToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Create org failed: HTTP {(int)response.StatusCode}: {errBody}");
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<InfluxOrg>(body, SerializerOptions)
            ?? throw new InvalidOperationException("Org created but response could not be parsed");
    }

    /// <summary>
    /// Ensure both NO buckets exist in the given org. Returns the bucket IDs needed for
    /// token scoping. If a bucket already exists with the requested name, reuses it.
    /// </summary>
    public async Task<BucketProvisionResult> EnsureBucketsAsync(
        string url,
        string adminToken,
        string orgId,
        string primaryBucket,
        string longtermBucket,
        CancellationToken ct = default)
    {
        var primary = await EnsureBucketAsync(url, adminToken, orgId, primaryBucket, retentionSeconds: 90 * 86400, ct);
        var longterm = await EnsureBucketAsync(url, adminToken, orgId, longtermBucket, retentionSeconds: 365 * 86400, ct);
        return new BucketProvisionResult
        {
            PrimaryBucketId = primary.Id,
            PrimaryBucketName = primary.Name,
            LongtermBucketId = longterm.Id,
            LongtermBucketName = longterm.Name
        };
    }

    private async Task<InfluxBucket> EnsureBucketAsync(string url, string adminToken, string orgId, string name, long retentionSeconds, CancellationToken ct)
    {
        using var http = _httpClientFactory.CreateClient();
        var listUri = $"{NormalizeUrl(url)}/api/v2/buckets?orgID={Uri.EscapeDataString(orgId)}&name={Uri.EscapeDataString(name)}";
        var listReq = new HttpRequestMessage(HttpMethod.Get, listUri);
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Token", adminToken);
        using var listResp = await http.SendAsync(listReq, ct);
        if (listResp.IsSuccessStatusCode)
        {
            var listBody = await listResp.Content.ReadAsStringAsync(ct);
            var existing = JsonSerializer.Deserialize<InfluxBucketList>(listBody, SerializerOptions);
            var found = existing?.Buckets?.FirstOrDefault();
            if (found != null) return found;
        }

        var createReq = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl(url) + "/api/v2/buckets")
        {
            Content = JsonContent(new
            {
                orgID = orgId,
                name,
                retentionRules = new[] { new { type = "expire", everySeconds = retentionSeconds } }
            })
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Token", adminToken);
        using var createResp = await http.SendAsync(createReq, ct);
        if (!createResp.IsSuccessStatusCode)
        {
            var errBody = await createResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Create bucket {name} failed: HTTP {(int)createResp.StatusCode}: {errBody}");
        }
        var body = await createResp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<InfluxBucket>(body, SerializerOptions)
            ?? throw new InvalidOperationException("Bucket created but response could not be parsed");
    }

    /// <summary>
    /// Create a new InfluxDB authorization scoped to read+write on the two NO buckets only.
    /// This is the token NO will store and use for ongoing operation — the admin token is
    /// discarded after this returns.
    /// </summary>
    public async Task<ScopedTokenResult> CreateScopedTokenAsync(
        string url,
        string adminToken,
        string orgId,
        string primaryBucketId,
        string longtermBucketId,
        string description = "Network Optimizer (read+write on monitoring buckets)",
        CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateClient();
        var permissions = new List<object>();
        foreach (var bucketId in new[] { primaryBucketId, longtermBucketId })
        {
            permissions.Add(new { action = "read", resource = new { type = "buckets", id = bucketId, orgID = orgId } });
            permissions.Add(new { action = "write", resource = new { type = "buckets", id = bucketId, orgID = orgId } });
        }
        var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl(url) + "/api/v2/authorizations")
        {
            Content = JsonContent(new
            {
                orgID = orgId,
                description,
                permissions
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", adminToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Create token failed: HTTP {(int)response.StatusCode}: {errBody}");
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        var auth = JsonSerializer.Deserialize<InfluxAuthorization>(body, SerializerOptions);
        if (auth == null || string.IsNullOrEmpty(auth.Token))
            throw new InvalidOperationException("Token created but response could not be parsed");
        return new ScopedTokenResult { TokenId = auth.Id ?? string.Empty, Token = auth.Token };
    }

    /// <summary>
    /// Resolve an org's ID by name using an arbitrary token. Returns null if the
    /// token can't list orgs (e.g. a per-bucket scoped token returns 401/403) or the
    /// named org isn't visible. Used to test whether the shared token is capable of
    /// bucket management before deciding to prompt for an all-access token.
    /// </summary>
    public async Task<string?> TryResolveOrgIdAsync(string url, string token, string orgName, CancellationToken ct = default)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, NormalizeUrl(url) + "/api/v2/orgs");
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            var list = JsonSerializer.Deserialize<InfluxOrgList>(body, SerializerOptions);
            return list?.Orgs?.FirstOrDefault(o =>
                string.Equals(o.Name, orgName, StringComparison.OrdinalIgnoreCase))?.Id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mint an authorization scoped to read+write on ALL buckets in the org, plus read
    /// on the org itself so the holder can resolve the org ID to create new buckets.
    /// This becomes the single shared credential once an install goes multi-site: it can
    /// create and write every site's buckets. Broader than the per-bucket token from
    /// <see cref="CreateScopedTokenAsync"/>, far narrower than an all-access token
    /// (no users, tasks, dashboards, other-org access). The read:orgs grant is scoped to
    /// the specific org ID - an org-wide read:orgs exceeds what a normal all-access token
    /// holds and InfluxDB refuses to mint it.
    /// </summary>
    public async Task<ScopedTokenResult> CreateOrgScopedTokenAsync(
        string url,
        string adminToken,
        string orgId,
        string description = "Network Optimizer (org-scoped: read+write + create buckets)",
        CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateClient();
        var permissions = new object[]
        {
            new { action = "read", resource = new { type = "orgs", id = orgId } },
            new { action = "read", resource = new { type = "buckets", orgID = orgId } },
            new { action = "write", resource = new { type = "buckets", orgID = orgId } },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, NormalizeUrl(url) + "/api/v2/authorizations")
        {
            Content = JsonContent(new { orgID = orgId, description, permissions })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", adminToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Create org-scoped token failed: HTTP {(int)response.StatusCode}: {errBody}");
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        var auth = JsonSerializer.Deserialize<InfluxAuthorization>(body, SerializerOptions);
        if (auth == null || string.IsNullOrEmpty(auth.Token))
            throw new InvalidOperationException("Token created but response could not be parsed");
        return new ScopedTokenResult { TokenId = auth.Id ?? string.Empty, Token = auth.Token };
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string NormalizeUrl(string url) => url.TrimEnd('/');

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---- Response DTOs ----

    public record UrlProbeResult
    {
        public required string Url { get; init; }
        public bool Reachable { get; init; }
        public string? Version { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<string>? CandidatesTried { get; init; }
    }

    public record TokenValidationResult
    {
        public bool Valid { get; init; }
        public string? UserName { get; init; }
        public string? Error { get; init; }
    }

    public record BucketProvisionResult
    {
        public required string PrimaryBucketId { get; init; }
        public required string PrimaryBucketName { get; init; }
        public required string LongtermBucketId { get; init; }
        public required string LongtermBucketName { get; init; }
    }

    public record ScopedTokenResult
    {
        public required string TokenId { get; init; }
        public required string Token { get; init; }
    }

    public class InfluxUser
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    public class InfluxOrg
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    private class InfluxOrgList
    {
        public List<InfluxOrg>? Orgs { get; set; }
    }

    public class InfluxBucket
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? OrgID { get; set; }
    }

    private class InfluxBucketList
    {
        public List<InfluxBucket>? Buckets { get; set; }
    }

    private class InfluxAuthorization
    {
        public string? Id { get; set; }
        public string? Token { get; set; }
    }
}
