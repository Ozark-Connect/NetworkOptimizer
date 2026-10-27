using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Tests for <see cref="RicherlinkOntProvider"/>. Fixtures are trimmed from a signed-in capture
/// of an RL821GWV-D, with the serial and batch number replaced by generic values.
/// </summary>
public class RicherlinkOntProviderTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "OntProviders", "Fixtures");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureDir, name));

    private static string NetworkPage => Fixture("richerlink-sta-network.html");
    private static string DevicePage => Fixture("richerlink-sta-device.html");
    private static string ContentPage => Fixture("richerlink-content.html");

    // Synthetic: no login page has been captured yet. Shaped like the e8 firmware family's.
    private const string SignInPage = """
        <html><head><title>Login</title></head>
        <body><form name="loginForm" method="post" action="/cgi-bin/login.cgi">
        <input type="text" name="username"><input type="password" name="password">
        </form></body></html>
        """;

    private static string NetworkPageWith(string field, string value) => With(NetworkPage, field, value);

    private static string With(string html, string field, string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            html, $@"(\b(?:var |this\.){field}\s*=\s*')[^']*(')", $"${{1}}{value}${{2}}");

    // --- sta-network.asp ---

    [Fact]
    public void ApplyNetworkPage_Fixture_ConvertsSff8472Optics()
    {
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyNetworkPage(NetworkPage, stats).Should().BeTrue();

        stats.RxPowerDbm.Should().Be(-22.37);
        stats.TxPowerDbm.Should().Be(2.74);
        stats.TemperatureC.Should().Be(69.9);
        stats.VoltageV.Should().Be(3.27);
        stats.BiasMa.Should().Be(12.55);
    }

    [Fact]
    public void ApplyNetworkPage_Fixture_ReportsGponLinkUp()
    {
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyNetworkPage(NetworkPage, stats);

        stats.PonType.Should().Be("GPON");
        stats.PonLinkStatus.Should().Be(PonLinkState.Operation);
        stats.OperationalStatus.Should().Be("Up");
        stats.LinkState.Should().Be("Connected (O5)");
        stats.Pon.Should().BeNull("this device cannot feed the PON detail charts");
    }

    [Fact]
    public void ApplyNetworkPage_NoLink_LeavesOpticsNullAndReportsDown()
    {
        var html = With(NetworkPageWith("LinkSta", "0"), "trafficstate", "down");
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyNetworkPage(html, stats);

        stats.RxPowerDbm.Should().BeNull();
        stats.TxPowerDbm.Should().BeNull();
        stats.TemperatureC.Should().BeNull();
        stats.VoltageV.Should().BeNull();
        stats.BiasMa.Should().BeNull();
        stats.PonType.Should().BeNull();
        stats.PonLinkStatus.Should().Be(PonLinkState.Unknown);
        stats.OperationalStatus.Should().Be("Down");
        stats.LinkState.Should().Be("Down");
    }

    [Fact]
    public void ApplyNetworkPage_TrafficDown_ReportsDown()
    {
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyNetworkPage(NetworkPageWith("trafficstate", "down"), stats);

        stats.OperationalStatus.Should().Be("Down");
        stats.PonLinkStatus.Should().Be(PonLinkState.Unknown);
    }

    [Fact]
    public void ApplyNetworkPage_NoLight_RxIsNullNotNegativeInfinity()
    {
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyNetworkPage(NetworkPageWith("RxPower", "0"), stats);

        stats.RxPowerDbm.Should().BeNull();
        stats.TxPowerDbm.Should().Be(2.74);
    }

    [Fact]
    public void ApplyNetworkPage_NegativeTemperature_UsesTwosComplement()
    {
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyNetworkPage(NetworkPageWith("Temperature", "65280"), stats);

        stats.TemperatureC.Should().Be(-1.0);
    }

    [Fact]
    public void ApplyNetworkPage_LinkSta2_ReportsEpon()
    {
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyNetworkPage(NetworkPageWith("LinkSta", "2"), stats);

        stats.PonType.Should().Be("EPON");
    }

    [Fact]
    public void ApplyNetworkPage_MissingFields_LeavesDefaults()
    {
        const string html = """
            <script>
            function PonInfoClass()
            {
                var RxPower = 'garbage';
                this.trafficstate = 'up';
            }
            var PonInfo = new PonInfoClass();
            </script>
            """;
        var stats = new OntStats();

        var act = () => RicherlinkOntProvider.ApplyNetworkPage(html, stats);

        act.Should().NotThrow();
        stats.RxPowerDbm.Should().BeNull();
        stats.TxPowerDbm.Should().BeNull();
        stats.TemperatureC.Should().BeNull();
        stats.VoltageV.Should().BeNull();
        stats.BiasMa.Should().BeNull();
        stats.PonType.Should().BeNull();
        stats.OperationalStatus.Should().Be("Up");
    }

    [Fact]
    public void ApplyNetworkPage_NoTrafficState_LeavesLinkUnknown()
    {
        const string html = "function PonInfoClass() { var RxPower = '58'; } new PonInfoClass();";
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyNetworkPage(html, stats);

        stats.RxPowerDbm.Should().Be(-22.37);
        stats.PonLinkStatus.Should().Be(PonLinkState.Unknown);
        stats.OperationalStatus.Should().BeNull();
        stats.LinkState.Should().BeNull();
    }

    [Fact]
    public void ApplyNetworkPage_NoPonInfoBlock_ReturnsFalse()
    {
        RicherlinkOntProvider.ApplyNetworkPage(DevicePage, new OntStats()).Should().BeFalse();
    }

    // --- Conversions ---

    [Theory]
    [InlineData("58", -22.37)]
    [InlineData("18800", 2.74)]
    [InlineData("10000", 0.0)]
    public void RawPowerToDbm_Converts(string raw, double expected) =>
        RicherlinkOntProvider.RawPowerToDbm(raw).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("N/A")]
    public void RawPowerToDbm_UnusableInput_ReturnsNull(string? raw) =>
        RicherlinkOntProvider.RawPowerToDbm(raw).Should().BeNull();

    [Theory]
    [InlineData("17898", 69.9)]
    [InlineData("0", 0.0)]
    [InlineData("65280", -1.0)]
    [InlineData("32768", -128.0)]
    public void RawTemperatureToC_Converts(string raw, double expected) =>
        RicherlinkOntProvider.RawTemperatureToC(raw).Should().Be(expected);

    [Fact]
    public void RawTemperatureToC_OutOfRange_ReturnsNull() =>
        RicherlinkOntProvider.RawTemperatureToC("65536").Should().BeNull();

    [Fact]
    public void RawVoltageAndBias_Convert()
    {
        RicherlinkOntProvider.RawVoltageToV("32724").Should().Be(3.27);
        RicherlinkOntProvider.RawBiasToMa("6275").Should().Be(12.55);
        RicherlinkOntProvider.RawVoltageToV("-").Should().BeNull();
        RicherlinkOntProvider.RawBiasToMa(null).Should().BeNull();
    }

    // --- sta-device.asp and content.asp ---

    [Fact]
    public void ParseDevicePage_Fixture_ReadsSnAndVersions()
    {
        var info = RicherlinkOntProvider.ParseDevicePage(DevicePage);

        info.GponSn.Should().Be("RLGM00000000");
        info.HardwareVersion.Should().Be("V2.0");
        info.SoftwareVersion.Should().Be("V2.1.5-21783");
    }

    [Fact]
    public void ParseDevicePage_IgnoresTemplateDefaults()
    {
        var info = RicherlinkOntProvider.ParseDevicePage(DevicePage);

        info.HardwareVersion.Should().NotBe("HG530RRA.VER.C");
        info.SoftwareVersion.Should().NotBe("V100R001C01B010");
    }

    [Fact]
    public void ParseDevicePage_MissingRows_ReturnsNulls()
    {
        var info = RicherlinkOntProvider.ParseDevicePage("<html><body>nothing here</body></html>");

        info.GponSn.Should().BeNull();
        info.HardwareVersion.Should().BeNull();
        info.SoftwareVersion.Should().BeNull();
    }

    [Fact]
    public void ApplyDeviceInfo_MapsSnAndHardwareVersion()
    {
        var stats = new OntStats();

        RicherlinkOntProvider.ApplyDeviceInfo(RicherlinkOntProvider.ParseDevicePage(DevicePage), stats);

        stats.VendorSn.Should().Be("RLGM00000000");
        stats.VendorPn.Should().Be("V2.0");
    }

    [Fact]
    public void ParseModelName_ContentFixture_ReadsModel()
    {
        RicherlinkOntProvider.ParseModelName(ContentPage).Should().Be("RL821GWV-D");
        RicherlinkOntProvider.ParseModelName("<html></html>").Should().BeNull();
    }

    // --- Sign-in detection ---

    [Fact]
    public void ClassifyPage_DataPages_AreData()
    {
        RicherlinkOntProvider.ClassifyPage(NetworkPage, "PonInfoClass").Should().Be(RicherlinkOntProvider.PageKind.Data);
        RicherlinkOntProvider.ClassifyPage(DevicePage, "GPON SN").Should().Be(RicherlinkOntProvider.PageKind.Data);
    }

    [Theory]
    [InlineData(SignInPage)]
    [InlineData("<script>top.location.replace('/cgi-bin/index.asp');</script>")]
    [InlineData("<form><input type=password name=pw></form>")]
    public void ClassifyPage_SignInPages_AreSignIn(string html) =>
        RicherlinkOntProvider.ClassifyPage(html, "PonInfoClass").Should().Be(RicherlinkOntProvider.PageKind.SignIn);

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Some other device</body></html>")]
    public void ClassifyPage_OtherPages_AreUnrecognized(string html) =>
        RicherlinkOntProvider.ClassifyPage(html, "PonInfoClass").Should().Be(RicherlinkOntProvider.PageKind.Unrecognized);

    [Fact]
    public void ClassifyPage_SignedInPageWithoutMarker_IsUnrecognized()
    {
        // A signed-in page links logout.cgi, so it is never mistaken for a sign-in page.
        RicherlinkOntProvider.ClassifyPage(DevicePage, "PonInfoClass").Should().Be(RicherlinkOntProvider.PageKind.Unrecognized);
    }

    // --- End to end against a stub device ---

    [Fact]
    public async Task PollAsync_OpenSession_ReadsPagesWithoutSigningInOrOut()
    {
        using var server = new StubOnt(new()
        {
            ["/cgi-bin/sta-network.asp"] = (200, NetworkPage),
            ["/cgi-bin/sta-device.asp"] = (200, DevicePage),
            ["/cgi-bin/content.asp"] = (200, ContentPage),
        });

        var result = await NewProvider().PollAsync(server.Context());

        result.FailureReason.Should().BeNull();
        var stats = result.Stats!;
        stats.RxPowerDbm.Should().Be(-22.37);
        stats.TxPowerDbm.Should().Be(2.74);
        stats.DeviceModel.Should().Be("RL821GWV-D");
        stats.VendorName.Should().Be("Richerlink");
        stats.VendorSn.Should().Be("RLGM00000000");
        stats.VendorPn.Should().Be("V2.0");
        stats.OperationalStatus.Should().Be("Up");
        // A session someone else opened from the gateway's address must stay open.
        server.Paths.Should().NotContain([SignIn, SignOut]);
    }

    [Fact]
    public async Task PollAsync_IdentityPagesFail_StillReturnsOptics()
    {
        using var server = new StubOnt(new()
        {
            ["/cgi-bin/sta-network.asp"] = (200, NetworkPage),
        });

        var result = await NewProvider().PollAsync(server.Context());

        result.Stats.Should().NotBeNull();
        result.Stats!.DeviceModel.Should().Be(RicherlinkOntProvider.DefaultModel);
        result.Stats.VendorSn.Should().BeNull();
        result.Stats.RxPowerDbm.Should().Be(-22.37);
    }

    [Fact]
    public async Task PollAsync_SignInRequired_SignsInReadsAndSignsOut()
    {
        using var server = SignInOnt("User1", "p@ss&wd");

        var result = await NewProvider().PollAsync(server.Context("User1", "p@ss&wd"));

        result.FailureReason.Should().BeNull();
        result.Stats!.RxPowerDbm.Should().Be(-22.37);
        result.Stats.VendorSn.Should().Be("RLGM00000000");
        server.Requests.Select(r => $"{r.Method} {r.Path}").Should().Equal(
            "GET /cgi-bin/sta-network.asp", $"GET {SignIn}", $"POST {SignIn}", "GET /cgi-bin/sta-network.asp",
            "GET /cgi-bin/sta-device.asp", "GET /cgi-bin/content.asp", $"GET {SignOut}");
        server.SignedIn.Should().BeFalse("the ONT allows one session, so the poll frees it");
    }

    [Fact]
    public async Task PollAsync_SignIn_SendsTheLoginPageFieldsAndCookies()
    {
        using var server = SignInOnt("User1", "p@ss&wd");

        await NewProvider().PollAsync(server.Context("User1", "p@ss&wd"));

        var post = server.Requests.Single(r => r.Method == "POST");
        post.Body.Should().Be(RicherlinkOntProvider.BuildSignInForm("User1", "p@ss&wd"));
        post.Cookie.Should().Contain($"SESSIONID={StubOnt.SessionId}")
            .And.Contain("UID=User1").And.Contain("PSW=p@ss&wd").And.Contain("LoginTimes=1");
    }

    [Fact]
    public void BuildSignInForm_MatchesTheLoginPageForm() =>
        RicherlinkOntProvider.BuildSignInForm("User1", "a b&c").Should().Be(
            "Username=User1&Logoff=0&hLoginTimes=1&hLoginTimes_Zero=0&value_one=1&Password1=a%20b%26c" +
            "&Password2=a%20b%26c&logintype=usr&LoginFailTime=1&hLoginFailTime=1&Password=a%20b%26c");

    [Fact]
    public async Task PollAsync_SignInRequiredWithoutCredentials_AsksForThem()
    {
        using var server = SignInOnt("User1", "pw");

        var result = await NewProvider().PollAsync(server.Context());

        result.FailureReason.Should().Be(RicherlinkOntProvider.CredentialsNeededMessage("127.0.0.1"));
        server.Paths.Should().NotContain([SignIn, SignOut]);
    }

    [Fact]
    public async Task PollAsync_SignInPageWithoutCredentials_AsksForThem()
    {
        using var server = new StubOnt(new() { ["/cgi-bin/sta-network.asp"] = (200, SignInPage) });

        var result = await NewProvider().PollAsync(server.Context());

        result.FailureReason.Should().Be(RicherlinkOntProvider.CredentialsNeededMessage("127.0.0.1"));
    }

    [Fact]
    public async Task PollAsync_RejectedSignIn_HoldsTheNextTry()
    {
        // Three failed sign-ins lock the ONT's web page, so a rejected password is not resent each poll.
        using var server = SignInOnt("User1", "right");
        var provider = NewProvider();

        var first = await provider.PollAsync(server.Context("User1", "wrong"));
        first.FailureReason.Should().Be(RicherlinkOntProvider.SignInRejectedMessage("127.0.0.1"));
        server.Paths.Should().EndWith(SignOut);

        var second = await provider.PollAsync(server.Context("User1", "wrong"));
        second.FailureReason.Should().StartWith(RicherlinkOntProvider.SignInRejectedMessage("127.0.0.1"))
            .And.Contain("The next sign-in is in 5 min");

        server.Requests.Count(r => r.Method == "POST").Should().Be(1);
    }

    [Fact]
    public async Task PollAsync_ChangedPassword_IsTriedDuringTheHold()
    {
        using var server = SignInOnt("User1", "right");
        var provider = NewProvider();
        await provider.PollAsync(server.Context("User1", "wrong"));

        var result = await provider.PollAsync(server.Context("User1", "right"));

        result.FailureReason.Should().BeNull();
        result.Stats!.RxPowerDbm.Should().Be(-22.37);
    }

    [Fact]
    public async Task PollAsync_MissingPage_SaysTheAddressIsWrong()
    {
        using var server = new StubOnt(new());

        var result = await NewProvider().PollAsync(server.Context());

        result.FailureReason.Should().Contain("not on the address this model is polled at");
    }

    [Fact]
    public async Task PollAsync_NothingListening_SaysTheConnectionWasRefused()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await NewProvider().PollAsync(new OntPollContext { Id = 1, Name = "Richerlink", Host = "127.0.0.1", Port = port });

        result.FailureReason.Should().Contain("refused the connection");
    }

    [Fact]
    public async Task TestConnectionAsync_SignInRequired_SignsInAndOut()
    {
        using var server = SignInOnt("User1", "pw");

        var (success, message) = await NewProvider().TestConnectionAsync(server.Context("User1", "pw"));

        success.Should().BeTrue();
        message.Should().Be("Connected (HTTP) - RX: -22.37 dBm, TX: 2.74 dBm");
        server.Paths.Should().EndWith(SignOut);
        server.SignedIn.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_RejectedSignIn_ReportsIt()
    {
        using var server = SignInOnt("User1", "right");

        var (success, message) = await NewProvider().TestConnectionAsync(server.Context("User1", "wrong"));

        success.Should().BeFalse();
        message.Should().Be(RicherlinkOntProvider.SignInRejectedMessage("127.0.0.1"));
    }

    [Fact]
    public async Task TestConnectionAsync_DataPage_ReportsOptics()
    {
        using var server = new StubOnt(new() { ["/cgi-bin/sta-network.asp"] = (200, NetworkPage) });

        var (success, message) = await NewProvider().TestConnectionAsync(server.Context());

        success.Should().BeTrue();
        message.Should().Be("Connected (HTTP) - RX: -22.37 dBm, TX: 2.74 dBm");
    }

    [Fact]
    public void ProviderIdentity_MatchesSettingsOption()
    {
        var provider = NewProvider();

        provider.ProviderKey.Should().Be("richerlink-ont");
        provider.DisplayName.Should().Be("Richerlink RL821GWV-D (HTTP)");
    }

    private const string SignIn = RicherlinkOntProvider.SignInPath;
    private const string SignOut = RicherlinkOntProvider.SignOutPath;

    private static RicherlinkOntProvider NewProvider() => new(NullLogger<RicherlinkOntProvider>.Instance);

    private static StubOnt SignInOnt(string username, string password) => new(new()
    {
        ["/cgi-bin/sta-network.asp"] = (200, NetworkPage),
        ["/cgi-bin/sta-device.asp"] = (200, DevicePage),
        ["/cgi-bin/content.asp"] = (200, ContentPage),
    }, (username, password));

    /// <summary>
    /// Serves fixed pages with the device's own <c>charset=GB2312</c> header. Unknown paths answer
    /// 404. With <c>login</c> set it acts like the ONT's session: data pages answer 401 until a
    /// POST to index2.asp carries the SESSIONID it issued, the UID/PSW cookies, and the matching
    /// form fields, and logout.cgi ends the session.
    /// </summary>
    private sealed class StubOnt : IDisposable
    {
        public const string SessionId = "0123abcd";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<string, (int Status, string Body)> _pages;
        private readonly (string Username, string Password)? _login;
        private readonly List<(string Method, string Path, string Cookie, string Body)> _requests = [];

        public int Port { get; }

        public bool SignedIn { get; private set; }

        public IReadOnlyList<(string Method, string Path, string Cookie, string Body)> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public IReadOnlyList<string> Paths => Requests.Select(r => r.Path).ToList();

        public StubOnt(Dictionary<string, (int Status, string Body)> pages, (string Username, string Password)? login = null)
        {
            _pages = pages;
            _login = login;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public OntPollContext Context(string? username = null, string? password = null) => new()
        {
            Id = 1, Name = "Richerlink", Host = "127.0.0.1", Port = Port, Username = username, Password = password,
        };

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    await HandleAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var received = new StringBuilder();
            int headerEnd;
            while ((headerEnd = received.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                    return;
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var lines = received.ToString()[..headerEnd].Split("\r\n");
            string Header(string name) => lines.Skip(1)
                .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";
            var length = int.TryParse(Header("Content-Length"), out var n) ? n : 0;
            while (received.Length < headerEnd + 4 + length)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                    break;
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var method = lines[0].Split(' ')[0];
            var path = lines[0].Split(' ')[1];
            var cookie = Header("Cookie");
            var body = received.ToString()[(headerEnd + 4)..];
            lock (_requests) _requests.Add((method, path, cookie, body));

            var (status, page, setCookie) = Respond(method, path, cookie, body);
            var bodyBytes = Encoding.UTF8.GetBytes(page);
            var reason = status switch { 200 => "OK", 401 => "Unauthorized", _ => "Not Found" };
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-type: text/html;charset=GB2312\r\n" +
                       (setCookie != null ? $"Set-Cookie: {setCookie}; path=/\r\n" : "") +
                       $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token);
            await stream.WriteAsync(bodyBytes, _cts.Token);
        }

        private (int Status, string Body, string? SetCookie) Respond(string method, string path, string cookie, string body)
        {
            if (_login is { } login && path == SignIn)
            {
                if (method == "GET")
                    return (200, SignInPage, $"SESSIONID={SessionId}");

                var form = body.Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                var cookies = cookie.Split("; ").Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
                SignedIn = cookies.GetValueOrDefault("SESSIONID") == SessionId
                    && cookies.GetValueOrDefault("UID") == login.Username && cookies.GetValueOrDefault("PSW") == login.Password
                    && form.GetValueOrDefault("Username") == login.Username && form.GetValueOrDefault("Password") == login.Password;
                return (200, "<frameset></frameset>", null);
            }

            if (_login != null && path == SignOut)
            {
                SignedIn = false;
                return (200, "", null);
            }

            if (!_pages.TryGetValue(path, out var page))
                return (404, "", null);
            if (_login != null && !SignedIn)
                return (401, "<script>top.location='https://127.0.0.1/cgi-bin/index2.asp';</script>", null);
            return (page.Status, page.Body, null);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
