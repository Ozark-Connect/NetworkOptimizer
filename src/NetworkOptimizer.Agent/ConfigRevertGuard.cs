using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Change Control's agent-side revert. Holds the calls that undo a config apply, with the console
/// API key, in memory only. While the tunnel is up the server decides; past the deadline with the
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

    private static readonly HttpMessageInvoker StrictClient = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) });
    private static readonly HttpMessageInvoker LenientClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(8),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = delegate { return true; } },
    });

    private static HttpMessageInvoker DefaultHttp(bool ignoreTls) => ignoreTls ? LenientClient : StrictClient;

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
        lock (_lock) _armed[arm.PlanId] = new Armed { Arm = arm };
        Console.WriteLine($"Change Control: revert for plan {arm.PlanId} armed ({arm.Calls.Count} call(s))");
    }

    /// <summary>The server confirmed, cancelled, or acknowledged a report.</summary>
    public void OnConfirm(ConfigRevertConfirm confirm)
    {
        lock (_lock)
        {
            if (confirm.ReportReceived) _unacked.Remove(confirm.PlanId);
            else _armed.Remove(confirm.PlanId);
        }
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
            var failure = await CheckAsync(armed.Arm, ct);
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
            var (ok, failed, error) = await RevertAsync(armed.Arm, ct);
            lock (_lock) _armed.Remove(armed.Arm.PlanId);
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
    private async Task<string?> CheckAsync(ConfigRevertArm arm, CancellationToken ct)
    {
        var health = await SendAsync(arm, HttpMethod.Get, arm.HealthUrl, null, ct);
        if (health == null || !health.Value.Ok) return "the UniFi Console does not answer";

        if (arm.WatchDeviceMacs.Count > 0)
        {
            var devices = await SendAsync(arm, HttpMethod.Get, arm.DevicesUrl, null, ct);
            if (devices == null || !devices.Value.Ok) return "the device list does not load";
            var online = OnlineMacs(devices.Value.Body);
            var offline = arm.WatchDeviceMacs.Where(m => !online.Contains(m.ToLowerInvariant())).ToList();
            if (offline.Count > 0) return $"{offline.Count} watched device(s) offline";
        }

        if (arm.WatchInternet && !await _internetUp(ct)) return "no internet from the site";
        return null;
    }

    /// <summary>MACs in state 1 (connected) in a stat/device response.</summary>
    internal static HashSet<string> OnlineMacs(string body)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(body)?["data"] is not JsonArray data) return result;
            foreach (var d in data.OfType<JsonObject>())
                if (d["state"]?.ToString() == "1" && d["mac"]?.ToString() is { } mac) result.Add(mac.ToLowerInvariant());
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return result;
    }

    private async Task<(int Ok, int Failed, string? Error)> RevertAsync(ConfigRevertArm arm, CancellationToken ct)
    {
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
            var result = await SendAsync(arm, new HttpMethod(call.Method), url, string.IsNullOrEmpty(call.Body) ? null : Resolve(call.Body), ct);
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

    private async Task<(bool Ok, int Status, string Body)?> SendAsync(ConfigRevertArm arm, HttpMethod method, string url, string? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-API-KEY", arm.ApiKey);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http(arm.IgnoreTlsErrors).SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            return ((int)response.StatusCode is >= 200 and < 300, (int)response.StatusCode, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
