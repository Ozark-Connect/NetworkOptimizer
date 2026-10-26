using System.Net;
using FluentAssertions;
using NetworkOptimizer.Agent;
using NetworkOptimizer.AgentProtocol;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class ConfigRevertGuardTests
{
    private const string Health = "https://192.0.2.1/proxy/network/api/s/default/stat/health";
    private const string Devices = "https://192.0.2.1/proxy/network/api/s/default/stat/device";

    /// <summary>A console stand-in: answers health and devices, records every write.</summary>
    private sealed class FakeConsole : HttpMessageHandler
    {
        public bool ConsoleUp { get; set; } = true;
        public string DeviceState { get; set; } = "1";
        public List<(string Method, string Url, string? Body, string? Key)> Writes { get; } = [];
        public int Logins { get; private set; }
        public bool RequireCsrf { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!ConsoleUp) throw new HttpRequestException("unreachable");
            var url = request.RequestUri!.ToString();
            if (url == Login)
            {
                Logins++;
                var ok = Json("{}");
                ok.Headers.Add("X-Csrf-Token", "csrf-1");
                return ok;
            }
            var csrf = request.Headers.TryGetValues("X-Csrf-Token", out var c) ? c.First() : null;
            if (RequireCsrf && csrf != "csrf-1") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var key = request.Headers.TryGetValues("X-API-KEY", out var v) ? v.First() : csrf;
            if (url == Health) return Json("{\"data\":[{\"subsystem\":\"www\",\"status\":\"ok\"}]}");
            if (url == Devices) return Json($"{{\"data\":[{{\"mac\":\"aa:bb:cc:00:00:01\",\"state\":{DeviceState}}}]}}");
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            Writes.Add((request.Method.Method, url, body, key));
            return request.Method == HttpMethod.Post ? Json("{\"data\":[{\"_id\":\"new-net-id\"}]}") : Json("{\"data\":[]}");
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };
    }

    private const string Login = "https://192.0.2.1/api/auth/login";

    [Fact]
    public async Task PasswordConnection_SignsInOnce_AndSendsTheCsrfToken()
    {
        var (guard, console, sent, now) = Make();
        console.RequireCsrf = true;
        var arm = Arm(now[0].AddMinutes(-1));
        arm.ApiKey = "";
        arm.LoginUrl = Login;
        arm.Username = "admin";
        arm.Password = "secret";
        guard.OnArm(arm);

        await guard.TickAsync(CancellationToken.None);
        await guard.TickAsync(CancellationToken.None);
        console.DeviceState = "0";
        await guard.TickAsync(CancellationToken.None);

        console.Logins.Should().Be(1, "the session is reused across checks");
        console.Writes.Should().HaveCount(2).And.OnlyContain(w => w.Key == "csrf-1");
        sent.Should().Contain(m => m.ConfigRevertReport.State == ConfigRevert.Reverted);
    }

    [Fact]
    public async Task ProvisioningDevice_IsNotOffline()
    {
        var (guard, console, sent, now) = Make();
        console.DeviceState = "5";
        guard.OnArm(Arm(now[0].AddMinutes(-1)));

        await guard.TickAsync(CancellationToken.None);

        console.Writes.Should().BeEmpty();
        sent.Should().ContainSingle().Which.ConfigRevertReport.State.Should().Be(ConfigRevert.Held);
    }

    private static ConfigRevertArm Arm(DateTime deadline) => new()
    {
        PlanId = 7, DeadlineUnixMs = new DateTimeOffset(deadline).ToUnixTimeMilliseconds(), ApiKey = "test-key",
        HealthUrl = Health, DevicesUrl = Devices, WatchDeviceMacs = { "aa:bb:cc:00:00:01" },
        Calls =
        {
            new ConfigRevertCall { Key = "networkconf:create:IoT:x", Method = "POST", Url = "https://192.0.2.1/proxy/network/api/s/default/rest/networkconf", Body = "{\"name\":\"IoT\"}", CaptureId = true },
            new ConfigRevertCall { Key = "wlanconf:update:Home:w1", Method = "PUT", Url = "https://192.0.2.1/proxy/network/api/s/default/rest/wlanconf/w1", Body = "{\"networkconf_id\":\"@ref:networkconf:create:IoT:x\"}" },
        },
    };

    private static (ConfigRevertGuard Guard, FakeConsole Console, List<AgentMessage> Sent, DateTime[] Now) Make(bool tunnelUp = false)
    {
        var console = new FakeConsole();
        var sent = new List<AgentMessage>();
        var now = new[] { new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc) };
        var guard = new ConfigRevertGuard(_ => new HttpMessageInvoker(console), _ => Task.FromResult(true), () => now[0])
        {
            LiveSend = m => { sent.Add(m); return true; },
            TunnelUp = () => tunnelUp,
        };
        return (guard, console, sent, now);
    }

    [Fact]
    public async Task BeforeDeadline_DoesNothing()
    {
        var (guard, console, sent, now) = Make();
        guard.OnArm(Arm(now[0].AddMinutes(5)));

        await guard.TickAsync(CancellationToken.None);

        console.Writes.Should().BeEmpty();
        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task PastDeadline_HealthySite_HoldsAndReportsOnce()
    {
        var (guard, console, sent, now) = Make();
        guard.OnArm(Arm(now[0].AddMinutes(-1)));

        await guard.TickAsync(CancellationToken.None);
        await guard.TickAsync(CancellationToken.None);

        console.Writes.Should().BeEmpty();
        sent.Should().ContainSingle().Which.ConfigRevertReport.State.Should().Be(ConfigRevert.Held);
        guard.ArmedCount.Should().Be(1, "it keeps checking until the tunnel returns");
    }

    [Fact]
    public async Task PastDeadline_DeviceOffline_RevertsWithRefsResolved()
    {
        var (guard, console, sent, now) = Make();
        console.DeviceState = "0";
        guard.OnArm(Arm(now[0].AddMinutes(-1)));

        await guard.TickAsync(CancellationToken.None);

        console.Writes.Should().HaveCount(2);
        console.Writes.Should().OnlyContain(w => w.Key == "test-key");
        console.Writes[1].Body.Should().Contain("new-net-id");
        var report = sent.Should().ContainSingle().Which.ConfigRevertReport;
        report.State.Should().Be(ConfigRevert.Reverted);
        report.CallsOk.Should().Be(2);
        guard.ArmedCount.Should().Be(0, "the key leaves memory with the revert");
    }

    [Fact]
    public async Task TunnelUp_ServerDecides()
    {
        var (guard, console, sent, now) = Make(tunnelUp: true);
        console.DeviceState = "0";
        guard.OnArm(Arm(now[0].AddMinutes(-1)));

        await guard.TickAsync(CancellationToken.None);

        console.Writes.Should().BeEmpty();
        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_DropsTheRevert_AndAckStopsResends()
    {
        var (guard, console, sent, now) = Make();
        console.DeviceState = "0";
        guard.OnArm(Arm(now[0].AddMinutes(-1)));
        guard.OnArm(new ConfigRevertArm { PlanId = 8, DeadlineUnixMs = 0, ApiKey = "k", HealthUrl = Health, DevicesUrl = Devices });
        guard.OnConfirm(new ConfigRevertConfirm { PlanId = 8 });

        await guard.TickAsync(CancellationToken.None);
        guard.OnConnected();
        guard.OnConfirm(new ConfigRevertConfirm { PlanId = 7, ReportReceived = true });
        guard.OnConnected();

        sent.Should().HaveCount(2, "the report goes out once live and once on the reconnect before the ack");
        sent.Should().OnlyContain(m => m.ConfigRevertReport.PlanId == 7);
    }
}
