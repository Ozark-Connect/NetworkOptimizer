using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.CellularModemProviders;

/// <summary>
/// Session handling for <see cref="InseegoFxProvider"/>, driven through a stub ubus endpoint:
/// sign-in, token reuse, renewal on a refused session, and the rejected-password lockout guard.
/// </summary>
public class InseegoFxProviderTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string Password = "test-password";

    private static ModemPollContext Context(string password = Password, int port = 443) => new()
    {
        Id = 1,
        Name = "Test Gateway",
        Host = "192.0.2.1",
        Port = port,
        Password = password,
        ModemType = "Inseego FX",
    };

    private static string LoginOk(string token = Token) =>
        $$"""{"jsonrpc":"2.0","id":1,"result":[0,{"authenticated":1,"retry_count":5,"timeout":600,"session_token":"{{token}}"}]}""";

    private const string LoginRejected =
        """{"jsonrpc":"2.0","id":1,"result":[0,{"authenticated":0,"retry_count":4}]}""";

    private static string PollOk() =>
        """
        [
          {"jsonrpc":"2.0","id":1,"result":[0,{"tech":17,"roam":0,"rssi":0,"pci":123,"sinr":0,"rsrp":-95,"rsrq":-12,"snr":18,"oper_name":"TestCarrier","oper_id":"001010","cell_id":"1234567"}]},
          {"jsonrpc":"2.0","id":2,"result":[0,{"status":2}]},
          {"jsonrpc":"2.0","id":3,"result":[0,{"model":"FX4100"}]},
          {"jsonrpc":"2.0","id":4,"result":[0,{"model":"FX4120","manufacturer":"Inseego"}]},
          {"jsonrpc":"2.0","id":5,"result":[0,{"mifios_version":"1.2.3.4"}]}
        ]
        """;

    private const string PollDenied =
        """[{"jsonrpc":"2.0","id":1,"result":[6]},{"jsonrpc":"2.0","id":2,"result":[6]}]""";

    [Fact]
    public async Task PollAsync_SignsInThenPollsWithTheToken()
    {
        var handler = new StubUbus(req => req.IsLogin ? LoginOk() : PollOk());
        using var provider = Create(handler);

        var result = await provider.PollAsync(Context());

        result.Stats.Should().NotBeNull();
        result.Stats!.ModemModel.Should().Be("FX4100");
        result.Stats.Lte!.Rsrp.Should().Be(-95);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].IsLogin.Should().BeTrue();
        handler.Requests[0].Password.Should().Be(Password);
        handler.Requests[1].Session.Should().Be(Token);
    }

    [Fact]
    public async Task PollAsync_ReusesTheTokenAcrossPolls()
    {
        var handler = new StubUbus(req => req.IsLogin ? LoginOk() : PollOk());
        using var provider = Create(handler);

        await provider.PollAsync(Context());
        var second = await provider.PollAsync(Context());

        second.Stats.Should().NotBeNull();
        handler.Requests.Count(r => r.IsLogin).Should().Be(1);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task PollAsync_RefusedSession_SignsInAgainAndRetries()
    {
        var polls = 0;
        var logins = 0;
        var handler = new StubUbus(req =>
        {
            if (req.IsLogin)
                return LoginOk(++logins == 1 ? "old-token" : Token);
            // The first poll after the first sign-in finds the session expired.
            return ++polls == 2 ? PollDenied : PollOk();
        });
        using var provider = Create(handler);

        (await provider.PollAsync(Context())).Stats.Should().NotBeNull();
        var result = await provider.PollAsync(Context());

        result.Stats.Should().NotBeNull();
        logins.Should().Be(2);
        handler.Requests.Last().Session.Should().Be(Token);
    }

    [Fact]
    public async Task PollAsync_SessionRefusedEvenAfterFreshSignIn_Fails()
    {
        var handler = new StubUbus(req => req.IsLogin ? LoginOk() : PollDenied);
        using var provider = Create(handler);

        var result = await provider.PollAsync(Context());

        result.Stats.Should().BeNull();
        result.FailureReason.Should().Contain("refused the session");
        handler.Requests.Count(r => r.IsLogin).Should().Be(2);
    }

    [Fact]
    public async Task PollAsync_RejectedPassword_IsNotRetriedByLaterPolls()
    {
        var handler = new StubUbus(req => req.IsLogin ? LoginRejected : PollOk());
        using var provider = Create(handler);

        var first = await provider.PollAsync(Context());
        var second = await provider.PollAsync(Context());

        first.FailureReason.Should().Contain("rejected the admin password");
        second.FailureReason.Should().Contain("rejected the admin password");
        // Only the first poll reached the gateway: each failed sign-in runs down its retry_count.
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PollAsync_ChangedPassword_SignsInAgain()
    {
        var handler = new StubUbus(req => req.Password == "new-password" ? LoginOk() : req.IsLogin ? LoginRejected : PollOk());
        using var provider = Create(handler);

        await provider.PollAsync(Context());
        var result = await provider.PollAsync(Context(password: "new-password"));

        result.Stats.Should().NotBeNull();
        handler.Requests.Count(r => r.IsLogin).Should().Be(2);
    }

    [Fact]
    public async Task TestConnectionAsync_ClearsTheRejectedPasswordGuard()
    {
        var accept = false;
        var handler = new StubUbus(req => req.IsLogin ? (accept ? LoginOk() : LoginRejected) : PollOk());
        using var provider = Create(handler);

        await provider.PollAsync(Context());
        accept = true;  // e.g. the password was reset on the gateway itself
        var (success, message) = await provider.TestConnectionAsync(Context());

        success.Should().BeTrue();
        message.Should().Be("Detected Inseego FX4100 on TestCarrier");
        (await provider.PollAsync(Context())).Stats.Should().NotBeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_AlwaysSignsInFresh()
    {
        var handler = new StubUbus(req => req.IsLogin ? LoginOk() : PollOk());
        using var provider = Create(handler);

        await provider.PollAsync(Context());
        await provider.TestConnectionAsync(Context());

        handler.Requests.Count(r => r.IsLogin).Should().Be(2);
    }

    [Fact]
    public async Task PollAsync_NoPassword_FailsWithoutContactingTheGateway()
    {
        var handler = new StubUbus(_ => PollOk());
        using var provider = Create(handler);

        var result = await provider.PollAsync(Context(password: ""));

        result.Stats.Should().BeNull();
        result.FailureReason.Should().Contain("admin password is required");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_NoHost_Fails()
    {
        var handler = new StubUbus(_ => PollOk());
        using var provider = Create(handler);

        var result = await provider.PollAsync(new ModemPollContext { Id = 1, Name = "x", Host = "", Password = Password });

        result.FailureReason.Should().Be("No address is configured for this modem.");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_NoServiceStats_ReportsNoSignalData()
    {
        var handler = new StubUbus(req => req.IsLogin
            ? LoginOk()
            : """[{"jsonrpc":"2.0","id":2,"result":[0,{"status":2}]}]""");
        using var provider = Create(handler);

        var result = await provider.PollAsync(Context());

        result.FailureReason.Should().Be("192.0.2.1 answered but returned no signal data.");
    }

    [Fact]
    public async Task PollAsync_HtmlInsteadOfJson_ReportsUnreadableAnswer()
    {
        var handler = new StubUbus(_ => "<html><body>Not an Inseego gateway</body></html>");
        using var provider = Create(handler);

        var result = await provider.PollAsync(Context());

        result.FailureReason.Should().Be("192.0.2.1 answered, but not with stats this optimizer can read.");
    }

    [Fact]
    public async Task PollAsync_HttpError_ReportsTheStatus()
    {
        var handler = new StubUbus(_ => "", HttpStatusCode.InternalServerError);
        using var provider = Create(handler);

        var result = await provider.PollAsync(Context());

        result.FailureReason.Should().Contain("answered with an error of its own (500)");
    }

    [Theory]
    [InlineData(443, "https://192.0.2.1/ubus")]
    [InlineData(80, "https://192.0.2.1/ubus")]
    [InlineData(0, "https://192.0.2.1/ubus")]
    [InlineData(8443, "https://192.0.2.1:8443/ubus")]
    public async Task PollAsync_PostsToUbusOverHttps(int port, string expectedUrl)
    {
        var handler = new StubUbus(req => req.IsLogin ? LoginOk() : PollOk());
        using var provider = Create(handler);

        await provider.PollAsync(Context(port: port));

        handler.Requests.Should().OnlyContain(r => r.Url == expectedUrl && r.Method == "POST");
    }

    private static InseegoFxProvider Create(StubUbus handler) =>
        new(NullLogger<InseegoFxProvider>.Instance, handler);

    /// <summary>One request the stub received, decoded from its JSON-RPC body.</summary>
    private sealed record UbusRequest(string Url, string Method, bool IsLogin, string? Session, string? Password);

    /// <summary>
    /// Answers each POST with whatever the responder returns for it, and records what was sent.
    /// </summary>
    private sealed class StubUbus : HttpMessageHandler
    {
        private readonly Func<UbusRequest, string> _respond;
        private readonly HttpStatusCode _status;

        public List<UbusRequest> Requests { get; } = new();

        public StubUbus(Func<UbusRequest, string> respond, HttpStatusCode status = HttpStatusCode.OK)
        {
            _respond = respond;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            var p = first.GetProperty("params");
            var isLogin = p[1].GetString() == "webui.login" && p[2].GetString() == "authenticate";

            var req = new UbusRequest(
                request.RequestUri!.ToString(),
                request.Method.Method,
                isLogin,
                p[0].GetString(),
                isLogin ? p[3].GetProperty("password").GetString() : null);
            Requests.Add(req);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_respond(req), Encoding.UTF8, "application/json"),
            };
        }
    }
}
