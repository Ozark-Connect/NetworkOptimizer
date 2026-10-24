using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.UniFi;

/// <summary>Which API root a raw request goes to.</summary>
public enum UniFiApiScope
{
    /// <summary>The Network app's v1 site API: <c>api/s/{site}/...</c> (rest/*, stat/*, cmd/*, group/*).</summary>
    SiteV1,
    /// <summary>The Network app's v2 site API: <c>v2/api/site/{site}/...</c>.</summary>
    SiteV2,
    /// <summary>The Network app's own root (<c>proxy/network/...</c> on UniFi OS), such as <c>dl/autobackup/...</c>.</summary>
    NetworkApp,
    /// <summary>The UniFi OS console itself (<c>api/system</c>, <c>proxy/users/...</c>). UniFi OS only.</summary>
    Console,
}

/// <summary>A raw response: the HTTP status (0 when the request never completed) and the body.</summary>
public sealed record UniFiRawResponse(int Status, string Body)
{
    /// <summary>2xx.</summary>
    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>The <c>meta.msg</c> or v2 error code in the body, when there is one.</summary>
    public string? ErrorCode
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(Body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString();
                foreach (var name in new[] { "code", "errorCode", "message" })
                    if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
            }
            catch (JsonException)
            {
                // Not JSON: no code.
            }
            return null;
        }
    }
}

/// <summary>
/// One write the client sent: method, the path below the site root, the top-level field names of
/// the JSON body (never values), the response status, and the source the caller declared.
/// </summary>
public sealed record UniFiWriteRecord(
    DateTime At, string Method, UniFiApiScope Scope, string Path, IReadOnlyList<string> Fields,
    int Status, string? Source, string? ResponseId);

/// <summary>
/// Declares which Network Optimizer feature is writing, for the Change Control write journal.
/// Flows with the async call, so a caller wraps its writes in <c>using (UniFiWriteSource.Use("..."))</c>.
/// </summary>
public static class UniFiWriteSource
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The declared source for the current async flow, or null.</summary>
    public static string? Value => Current.Value;

    /// <summary>Sets the source until the returned handle is disposed.</summary>
    public static IDisposable Use(string source)
    {
        var previous = Current.Value;
        Current.Value = source;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

public partial class UniFiApiClient
{
    /// <summary>
    /// Raised after every config write this client sends (any PUT, DELETE, or PATCH, and POSTs that
    /// are not reads). Handlers must be cheap and must not throw; exceptions are swallowed.
    /// </summary>
    public event Action<UniFiWriteRecord>? WriteSent;

    /// <summary>The UniFi site name this client talks to ("default" on most consoles).</summary>
    public string SiteName => _site;

    /// <summary>
    /// Sends one request and returns the status and body without interpreting either: no throw on
    /// an HTTP error, so an apply engine can re-read state after a refused write (UniFi can refuse
    /// and still write). Re-authenticates once on an auth failure, like every other call.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "internal v1/v2 API paths")]
    public async Task<UniFiRawResponse> SendRawAsync(
        UniFiApiScope scope, HttpMethod method, string path, string? json = null,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken))
            return new UniFiRawResponse(0, "Could not sign in to the UniFi Console.");

        var url = BuildRawUrl(scope, path);
        HttpRequestMessage Build() => new(method, url)
        {
            Content = json == null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var result = await ExecuteRequestAsync(async () =>
        {
            using var request = Build();
            var response = await _httpClient!.SendAsync(request, cancellationToken);
            if (IsRecoverableAuthFailure(response.StatusCode))
            {
                _isAuthenticated = false;
                if (await LoginAsync(cancellationToken))
                {
                    response.Dispose();
                    using var retry = Build();
                    response = await _httpClient!.SendAsync(retry, cancellationToken);
                }
            }
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new UniFiRawResponse((int)response.StatusCode, body);
            }
        });
        return result ?? new UniFiRawResponse(0, "The UniFi Console is not answering.");
    }

    /// <summary>Sends one request and returns the raw bytes, for binary downloads (console backups).</summary>
    public async Task<(int Status, byte[] Bytes)> GetRawBytesAsync(
        UniFiApiScope scope, string path, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken))
            return (0, []);
        var url = BuildRawUrl(scope, path);
        var result = await ExecuteRequestAsync(async () =>
        {
            using var response = await _httpClient!.GetAsync(url, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new Tuple<int, byte[]>((int)response.StatusCode, bytes);
        });
        return result == null ? (0, []) : (result.Item1, result.Item2);
    }

    /// <summary>The absolute URL this client would call for a scope and path (the agent-side revert replays these).</summary>
    public string ResolveUrl(UniFiApiScope scope, string path) => BuildRawUrl(scope, path);

    /// <summary>
    /// The console API key, handed only to an On-Site Agent for a revert window and never stored there.
    /// Null on username/password connections, which cannot arm an agent revert.
    /// </summary>
    public string? ApiKeyForAgentRevert => _apiKey;

    /// <summary>Whether this client accepts the console's self-signed certificate.</summary>
    public bool IgnoresTlsErrors => _ignoreSSLErrors;

    private string BuildRawUrl(UniFiApiScope scope, string path)
    {
        path = path.TrimStart('/');
        return scope switch
        {
            UniFiApiScope.SiteV1 => BuildApiPath(path),
            UniFiApiScope.SiteV2 => BuildV2ApiPath($"site/{_site}/{path}"),
            UniFiApiScope.NetworkApp => BuildNetworkAppPath(path),
            UniFiApiScope.Console => $"{_controllerUrl}/{path}",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
    }

    private void RaiseWriteSent(UniFiWriteRecord record)
    {
        var handler = WriteSent;
        if (handler == null) return;
        try { handler(record); }
        catch (Exception ex) { _logger.LogDebug(ex, "A write-journal handler threw"); }
    }

    /// <summary>
    /// Classifies a request URL as a config write and splits out the path below the site root.
    /// Returns null for reads that use POST (stat queries, the system log, backup listing) and for
    /// sign-in traffic.
    /// </summary>
    internal static (UniFiApiScope Scope, string Path)? ClassifyWrite(HttpMethod method, Uri uri, string site)
    {
        if (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options)
            return null;

        var abs = uri.AbsolutePath;
        string? rel = null;
        var scope = UniFiApiScope.SiteV1;
        var v1 = $"/api/s/{site}/";
        var v2 = $"/v2/api/site/{site}/";
        var i = abs.IndexOf(v2, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            rel = abs[(i + v2.Length)..];
            scope = UniFiApiScope.SiteV2;
        }
        else if ((i = abs.IndexOf(v1, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            rel = abs[(i + v1.Length)..];
        }
        if (rel == null) return null;

        if (method == HttpMethod.Post)
        {
            if (rel.StartsWith("stat/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("system-log", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("cmd/backup", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("cmd/stat", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("report", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/query", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("traffic-flows", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return (scope, rel);
    }

    /// <summary>Top-level field names of a JSON object body, or the first element's for an array. Never values.</summary>
    internal static IReadOnlyList<string> TopLevelFieldNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                root = root.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
            if (root.ValueKind != JsonValueKind.Object) return [];
            return root.EnumerateObject().Select(p => p.Name).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The <c>_id</c> of the first object in a response body (v1 data array or v2 object).</summary>
    internal static string? FirstResponseId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                root = data;
            if (root.ValueKind == JsonValueKind.Array)
                root = root.EnumerateArray().FirstOrDefault();
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("_id", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sits under the HttpClient and reports every config write to <see cref="WriteSent"/>. Lives
    /// at the handler so every NO feature's writes are journaled without each feature opting in.
    /// </summary>
    private sealed class WriteObservingHandler(UniFiApiClient owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var write = request.RequestUri == null ? null : ClassifyWrite(request.Method, request.RequestUri, owner._site);
            if (write == null || owner.WriteSent == null)
                return await base.SendAsync(request, cancellationToken);

            string? body = null;
            if (request.Content != null)
            {
                try { body = await request.Content.ReadAsStringAsync(cancellationToken); }
                catch (InvalidOperationException) { /* non-buffered content: field names unknown */ }
            }
            var source = UniFiWriteSource.Value;
            var response = await base.SendAsync(request, cancellationToken);
            string? responseBody = null;
            if (response.Content != null && response.IsSuccessStatusCode)
            {
                try
                {
                    await response.Content.LoadIntoBufferAsync(cancellationToken);
                    responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { }
            }
            owner.RaiseWriteSent(new UniFiWriteRecord(
                DateTime.UtcNow, request.Method.Method, write.Value.Scope, write.Value.Path,
                TopLevelFieldNames(body), (int)response.StatusCode, source, FirstResponseId(responseBody)));
            return response;
        }
    }
}
