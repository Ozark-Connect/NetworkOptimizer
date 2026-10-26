using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Change Control's agent-side revert. Holds the calls that undo a config apply, with the console
/// credentials (API key, or username and password), in memory only. While the tunnel is up the server decides; past the deadline with the
/// tunnel down, the agent checks the console, the watched devices, and the internet, reverts if any
/// check fails, and holds while all pass (the fault is then upstream: the NO server or its ISP).
/// Reports wait for the server's acknowledgement and resend on every reconnect.
/// </summary>
public sealed class ConfigRevertGuard
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private sealed class Armed
    {
        public required ConfigRevertArm Arm { get; set; }
        public bool Held { get; set; }

        // One console session per armed plan: a sign-in per check would trip the console's login limit.
        public HttpMessageInvoker? Client { get; set; }
        public string? Csrf { get; set; }
        public bool SignedIn { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<long, Armed> _armed = [];
    private readonly Dictionary<long, ConfigRevertReport> _unacked = [];
    private readonly Func<bool, HttpMessageInvoker> _http;
    private readonly Func<CancellationToken, Task<bool>> _internetUp;
    private readonly Func<DateTime> _now;

    /// <summary>Sends on the live tunnel; null while it is down.</summary>
    public Func<AgentMessage, bool>? LiveSend { get; set; }

    /// <summary>Whether the tunnel to the server is up right now.</summary>
    public Func<bool> TunnelUp { get; set; } = () => false;

    /// <param name="http">An HTTP sender per TLS mode (true = accept the console's self-signed certificate).</param>
    /// <param name="internetUp">The internet check; defaults to TCP connects to two public resolvers.</param>
    /// <param name="now">Clock, for tests.</param>
    public ConfigRevertGuard(Func<bool, HttpMessageInvoker>? http = null, Func<CancellationToken, Task<bool>>? internetUp = null, Func<DateTime>? now = null)
    {
        _http = http ?? DefaultHttp;
        _internetUp = internetUp ?? DefaultInternetUpAsync;
        _now = now ?? (() => DateTime.UtcNow);
    }

    // Its own cookie jar per plan, for the username/password session.
    private static HttpMessageInvoker DefaultHttp(bool ignoreTls) => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(8),
        CookieContainer = new System.Net.CookieContainer(),
        SslOptions = ignoreTls
            ? new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = delegate { return true; } }
            : new System.Net.Security.SslClientAuthenticationOptions(),
    });

    /// <summary>
    /// Device states that mean a device is gone (stat/device state: 0 disconnected, 6 heartbeat
    /// missed, 9 inform error, 11 isolated). Provisioning, upgrading, and adopting are not offline:
    /// a gateway provisions for about two minutes after a config write.
    /// </summary>
    internal static readonly IReadOnlySet<string> OfflineStates = new HashSet<string> { "0", "6", "9", "11" };

    private static async Task<bool> DefaultInternetUpAsync(CancellationToken ct)
    {
        foreach (var host in new[] { "1.1.1.1", "9.9.9.9" })
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                await client.ConnectAsync(host, 443, timeout.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
            }
        }
        return false;
    }

    /// <summary>Plans currently armed, for tests and diagnostics.</summary>
    public int ArmedCount { get { lock (_lock) return _armed.Count; } }

    /// <summary>Takes or replaces the revert for a plan.</summary>
    public void OnArm(ConfigRevertArm arm)
    {
        lock (_lock)
        {
            Drop(arm.PlanId);
            _armed[arm.PlanId] = new Armed { Arm = arm };
        }
        Console.WriteLine($"Change Control: revert for plan {arm.PlanId} armed ({arm.Calls.Count} call(s))");
    }

    /// <summary>The server confirmed, cancelled, or acknowledged a report.</summary>
    public void OnConfirm(ConfigRevertConfirm confirm)
    {
        lock (_lock)
        {
            if (confirm.ReportReceived) _unacked.Remove(confirm.PlanId);
            else Drop(confirm.PlanId);
        }
    }

    /// <summary>Forgets a plan's revert, its credentials, and its session. Call under the lock.</summary>
    private void Drop(long planId)
    {
        if (_armed.Remove(planId, out var armed)) armed.Client?.Dispose();
    }

    /// <summary>Resends every report the server has not acknowledged (call after the tunnel opens).</summary>
    public void OnConnected()
    {
        List<ConfigRevertReport> pending;
        lock (_lock) pending = [.. _unacked.Values];
        foreach (var r in pending) LiveSend?.Invoke(new AgentMessage { ConfigRevertReport = r });
    }

    /// <summary>Runs the deadline checks until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Change Control revert check failed: {ex.Message}");
            }
            try { await Task.Delay(TickInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass: every plan past its deadline with the tunnel down is checked, then held or reverted.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (TunnelUp()) return;
        List<Armed> due;
        var nowMs = new DateTimeOffset(_now()).ToUnixTimeMilliseconds();
        lock (_lock) due = _armed.Values.Where(a => a.Arm.DeadlineUnixMs <= nowMs).ToList();
        foreach (var armed in due)
        {
            var failure = await CheckAsync(armed, ct);
            if (failure == null)
            {
                if (!armed.Held)
                {
                    armed.Held = true;
                    Report(armed.Arm.PlanId, ConfigRevert.Held, "Past the deadline with the server unreachable; the console, devices, and internet check out, so the change stays.", 0, 0);
                }
                continue;
            }

            Console.WriteLine($"Change Control: reverting plan {armed.Arm.PlanId}: {failure}");
            var (ok, failed, error) = await RevertAsync(armed, ct);
            lock (_lock) Drop(armed.Arm.PlanId);
            Report(armed.Arm.PlanId, failed == 0 ? ConfigRevert.Reverted : ConfigRevert.RevertFailed,
                failed == 0 ? $"Reverted by the On-Site Agent: {failure}" : $"The On-Site Agent's revert stopped: {error} ({failure})", ok, failed);
        }
    }

    private void Report(long planId, string state, string detail, int ok, int failed)
    {
        var report = new ConfigRevertReport
        {
            PlanId = planId, State = state, Detail = detail, CallsOk = ok, CallsFailed = failed,
            AtUnixMs = new DateTimeOffset(_now()).ToUnixTimeMilliseconds(),
        };
        lock (_lock) _unacked[planId] = report;
        LiveSend?.Invoke(new AgentMessage { ConfigRevertReport = report });
    }

    /// <summary>Null when every check passes, otherwise what failed.</summary>
    private async Task<string?> CheckAsync(Armed armed, CancellationToken ct)
    {
        var arm = armed.Arm;
        var health = await SendAsync(armed, HttpMethod.Get, arm.HealthUrl, null, ct);
        if (health == null || !health.Value.Ok) return "the UniFi Console does not answer";

        if (arm.WatchDeviceMacs.Count > 0)
        {
            var devices = await SendAsync(armed, HttpMethod.Get, arm.DevicesUrl, null, ct);
            if (devices == null || !devices.Value.Ok) return "the device list does not load";
            var offline = OfflineMacs(devices.Value.Body, arm.WatchDeviceMacs);
            if (offline.Count > 0) return $"{offline.Count} watched device(s) offline";
        }

        if (arm.WatchInternet && !await _internetUp(ct)) return "no internet from the site";
        return null;
    }

    /// <summary>Watched MACs that are gone: missing from stat/device, or in an offline state.</summary>
    internal static List<string> OfflineMacs(string body, IEnumerable<string> watched)
    {
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(body)?["data"] is JsonArray data)
                foreach (var d in data.OfType<JsonObject>())
                    if (d["mac"]?.ToString() is { } mac) state[mac] = d["state"]?.ToString() ?? "";
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return watched.Where(m => !state.TryGetValue(m, out var s) || OfflineStates.Contains(s)).ToList();
    }

    private async Task<(int Ok, int Failed, string? Error)> RevertAsync(Armed armed, CancellationToken ct)
    {
        var arm = armed.Arm;
        var created = new Dictionary<string, string>(StringComparer.Ordinal);
        string Resolve(string s)
        {
            foreach (var (key, id) in created) s = s.Replace(ConfigRevert.RefPrefix + key, id, StringComparison.Ordinal);
            return s;
        }

        int ok = 0, failed = 0;
        string? error = null;
        foreach (var call in arm.Calls)
        {
            var url = Resolve(call.Url);
            if (url.Contains(ConfigRevert.RefPrefix, StringComparison.Ordinal)) { failed++; error ??= $"{call.Key}: an earlier create did not land"; continue; }
            var result = await SendAsync(armed, new HttpMethod(call.Method), url, string.IsNullOrEmpty(call.Body) ? null : Resolve(call.Body), ct);
            var gone = call.Method == "DELETE" && result is { Status: 404 };
            if (result is { Ok: true } || gone)
            {
                ok++;
                if (call.CaptureId && result!.Value.Ok && NewId(result.Value.Body) is { } id) created[call.Key] = id;
            }
            else
            {
                failed++;
                error ??= $"{call.Key}: HTTP {result?.Status ?? 0}";
            }
        }
        return (ok, failed, error);
    }

    /// <summary>The created object's ID from a v1 envelope or a v2 object.</summary>
    internal static string? NewId(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject o && o["data"] is JsonArray data) node = data.FirstOrDefault();
            if (node is JsonArray arr) node = arr.FirstOrDefault();
            return node is JsonObject obj ? (obj["_id"] ?? obj["id"])?.ToString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private bool UsesPassword(ConfigRevertArm arm) => string.IsNullOrEmpty(arm.ApiKey) && !string.IsNullOrEmpty(arm.LoginUrl);

    /// <summary>
    /// Signs in with the username and password the way the server's own client does: POST the
    /// login URL, keep the session cookie, and send the X-Csrf-Token it returns on every call.
    /// </summary>
    private async Task<bool> SignInAsync(Armed armed, CancellationToken ct)
    {
        var arm = armed.Arm;
        var body = new JsonObject { ["username"] = arm.Username, ["password"] = arm.Password, ["remember"] = false, ["strict"] = true };
        var result = await RawSendAsync(armed, HttpMethod.Post, arm.LoginUrl, body.ToJsonString(), ct);
        armed.SignedIn = result is { Ok: true };
        return armed.SignedIn;
    }

    private async Task<(bool Ok, int Status, string Body)?> SendAsync(Armed armed, HttpMethod method, string url, string? body, CancellationToken ct)
    {
        if (UsesPassword(armed.Arm) && !armed.SignedIn && !await SignInAsync(armed, ct)) return null;
        var result = await RawSendAsync(armed, method, url, body, ct);
        // A session the console dropped gets one fresh sign-in, never a loop of them.
        if (result is { Status: 401 } && UsesPassword(armed.Arm) && await SignInAsync(armed, ct))
            result = await RawSendAsync(armed, method, url, body, ct);
        return result;
    }

    private async Task<(bool Ok, int Status, string Body)?> RawSendAsync(Armed armed, HttpMethod method, string url, string? body, CancellationToken ct)
    {
        try
        {
            armed.Client ??= _http(armed.Arm.IgnoreTlsErrors);
            using var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(armed.Arm.ApiKey)) request.Headers.Add("X-API-KEY", armed.Arm.ApiKey);
            else if (armed.Csrf != null) request.Headers.Add("X-Csrf-Token", armed.Csrf);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await armed.Client.SendAsync(request, timeout.Token);
            if (response.Headers.TryGetValues("X-Csrf-Token", out var csrf) && csrf.FirstOrDefault() is { Length: > 0 } token)
                armed.Csrf = token;
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            return ((int)response.StatusCode is >= 200 and < 300, (int)response.StatusCode, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
