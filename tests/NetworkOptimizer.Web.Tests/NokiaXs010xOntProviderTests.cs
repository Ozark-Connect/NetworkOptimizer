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

public class NokiaXs010xOntProviderTests
{
    // Shape captured from a live XS-010X-Q's /GponForm/getUpdateinfo response; identifying
    // serial/MAC replaced with generic placeholders per the no-PII-in-tests rule.
    private const string FullUpdateInfoJson = """
        {
          "CurrentPonPw" : "000000000000000000000000000000000000000000000000000000000000000000000000",
          "VendorID" : "ALCL",
          "VersionID" : "3FE49331AAAA01",
          "SerialNum" : "ALCLaabbccdd",
          "Mac" : "001122334455",
          "ActiveSwVer" : "3FE49337BOCK48",
          "StandbySwVer" : "3FE49337BOCK35",
          "RxOptPwr" : "-13.3"
        }
        """;

    [Fact]
    public void ApplyUpdateInfo_FullFixture_MapsAllFields()
    {
        var stats = new OntStats();

        NokiaXs010xOntProvider.ApplyUpdateInfo(FullUpdateInfoJson, stats);

        stats.RxPowerDbm.Should().BeApproximately(-13.3, 0.0001);
        stats.VendorName.Should().Be("ALCL");
        stats.VendorPn.Should().Be("3FE49331AAAA01");
        stats.VendorSn.Should().Be("ALCLaabbccdd");
        stats.PonType.Should().Be("XGS-PON");
        stats.OperationalStatus.Should().Be("Up");
        stats.LinkState.Should().Be("Up");
        stats.TxPowerDbm.Should().BeNull();
    }

    [Fact]
    public void ApplyUpdateInfo_MissingRxPower_LeavesStatusUnknown()
    {
        var stats = new OntStats();
        var json = """{"VendorID":"ALCL","SerialNum":"ALCLaabbccdd"}""";

        NokiaXs010xOntProvider.ApplyUpdateInfo(json, stats);

        stats.RxPowerDbm.Should().BeNull();
        stats.OperationalStatus.Should().BeNull();
        stats.LinkState.Should().BeNull();
        stats.VendorName.Should().Be("ALCL");
    }

    [Fact]
    public void ApplyUpdateInfo_MalformedJson_DoesNotThrowAndLeavesDefaults()
    {
        var stats = new OntStats();

        var act = () => NokiaXs010xOntProvider.ApplyUpdateInfo("not json", stats);

        act.Should().NotThrow();
        stats.RxPowerDbm.Should().BeNull();
        stats.OperationalStatus.Should().BeNull();
    }

    [Fact]
    public void ComputeCmt_MatchesLiveDeviceDigest()
    {
        // Verified against a live unit: sha256("admin" + "ea" + "1234").
        NokiaXs010xOntProvider.ComputeCmt("admin", "ea", "1234")
            .Should().Be("b7290cb39156057010fd604590fe9c01ee72d700cf20f301a51d8fdef3f22fc7");
    }

    [Fact]
    public void ParseLoginConfig_ReturnsNonceAndSalt()
    {
        var json = """
            {"XError":0,"XStopTime":300,"XPasswdTip":" ","nonce":"AbCdEfGhIjKlMnOpQrStUvWxYz012345","saltval":"ea"}
            """;

        var (nonce, salt) = NokiaXs010xOntProvider.ParseLoginConfig(json);

        nonce.Should().Be("AbCdEfGhIjKlMnOpQrStUvWxYz012345");
        salt.Should().Be("ea");
    }

    [Fact]
    public void ParseLoginConfig_MalformedJson_ReturnsNulls()
    {
        var (nonce, salt) = NokiaXs010xOntProvider.ParseLoginConfig("not json");

        nonce.Should().BeNull();
        salt.Should().BeNull();
    }

    // The exact User-Agent the tester-proven curl script sends (also the provider's constant).
    private const string CurlScriptUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

    [Fact]
    public void BuildCurlRequest_GetUpdateinfo_MatchesCapturedCurlFraming()
    {
        // Byte-for-byte template captured by replaying the tester's working curl script against
        // a local listener: Host, User-Agent, Accept, Cookie, Referer, Origin, X-Requested-With,
        // then Content-Length BEFORE Content-Type with no charset suffix, and no Connection header.
        var expected =
            "POST /GponForm/getUpdateinfo HTTP/1.1\r\n" +
            "Host: 192.0.2.1\r\n" +
            $"User-Agent: {CurlScriptUserAgent}\r\n" +
            "Accept: */*\r\n" +
            "Cookie: sessionid=deadbeefcafe0123\r\n" +
            "Referer: http://192.0.2.1/moreinfo.html\r\n" +
            "Origin: http://192.0.2.1\r\n" +
            "X-Requested-With: XMLHttpRequest\r\n" +
            "Content-Length: 11\r\n" +
            "Content-Type: application/x-www-form-urlencoded\r\n" +
            "\r\n" +
            "token=token";

        var bytes = NokiaXs010xOntProvider.BuildCurlRequest(
            "http://192.0.2.1", "/GponForm/getUpdateinfo", "deadbeefcafe0123", "/moreinfo.html", "token=token");

        System.Text.Encoding.ASCII.GetString(bytes).Should().Be(expected);
    }

    [Fact]
    public void BuildCurlRequest_LoginConfigWithoutCookie_OmitsCookieHeader()
    {
        var expected =
            "POST /GponForm/Login_GetConfig HTTP/1.1\r\n" +
            "Host: 192.0.2.1:8080\r\n" +
            $"User-Agent: {CurlScriptUserAgent}\r\n" +
            "Accept: */*\r\n" +
            "Referer: http://192.0.2.1:8080/login.html\r\n" +
            "Origin: http://192.0.2.1:8080\r\n" +
            "X-Requested-With: XMLHttpRequest\r\n" +
            "Content-Length: 11\r\n" +
            "Content-Type: application/x-www-form-urlencoded\r\n" +
            "\r\n" +
            "token=token";

        var bytes = NokiaXs010xOntProvider.BuildCurlRequest(
            "http://192.0.2.1:8080", "/GponForm/Login_GetConfig", cookieId: null, "/login.html", "token=token");

        System.Text.Encoding.ASCII.GetString(bytes).Should().Be(expected);
    }

    [Fact]
    public void BuildCurlRequest_PageGet_CarriesOnlyCookieAndReferer()
    {
        var expected =
            "GET /ponpasswd.html HTTP/1.1\r\n" +
            "Host: 192.0.2.1\r\n" +
            $"User-Agent: {CurlScriptUserAgent}\r\n" +
            "Accept: */*\r\n" +
            "Cookie: sessionid=deadbeefcafe0123\r\n" +
            "Referer: http://192.0.2.1/login.html\r\n" +
            "\r\n";

        var bytes = NokiaXs010xOntProvider.BuildCurlRequest(
            "http://192.0.2.1", "/ponpasswd.html", "deadbeefcafe0123", "/login.html", formBody: null);

        System.Text.Encoding.ASCII.GetString(bytes).Should().Be(expected);
    }

    [Fact]
    public void BuildCurlRequest_InitialLoginPageGet_HasNoCookieOrReferer()
    {
        var expected =
            "GET /login.html HTTP/1.1\r\n" +
            "Host: 192.0.2.1\r\n" +
            $"User-Agent: {CurlScriptUserAgent}\r\n" +
            "Accept: */*\r\n" +
            "\r\n";

        var bytes = NokiaXs010xOntProvider.BuildCurlRequest(
            "http://192.0.2.1", "/login.html", cookieId: null, refererPath: null, formBody: null);

        System.Text.Encoding.ASCII.GetString(bytes).Should().Be(expected);
    }

    // The device answers every GponForm call with Transfer-Encoding: chunked + Connection: close
    // (seen in both testers' HARs), so the raw reader has to de-chunk.
    private const string ChunkedDeviceResponse =
        "HTTP/1.1 200 OK\r\nServer: nginx\r\nContent-Type: text/html\r\n" +
        "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
        "8\r\n{\"RxOptP\r\nc\r\nwr\":\"-13.3\"}\r\n0\r\n\r\n";

    [Fact]
    public void ParseRawHttpResponse_ChunkedBody_DecodesStatusAndBody()
    {
        var (status, body) = NokiaXs010xOntProvider.ParseRawHttpResponse(
            System.Text.Encoding.ASCII.GetBytes(ChunkedDeviceResponse));

        status.Should().Be(200);
        body.Should().Be("""{"RxOptPwr":"-13.3"}""");
    }

    [Fact]
    public void ParseRawHttpResponse_ContentLengthBody_TrimsToDeclaredLength()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 401 Unauthorized\r\nContent-Length: 5\r\n\r\nhelloEXTRA");

        var (status, body) = NokiaXs010xOntProvider.ParseRawHttpResponse(raw);

        status.Should().Be(401);
        body.Should().Be("hello");
    }

    [Fact]
    public void ParseRawHttpResponse_TruncatedChunkedBody_ReturnsBytesReceivedSoFar()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n8\r\n{\"RxOptP");

        var (status, body) = NokiaXs010xOntProvider.ParseRawHttpResponse(raw);

        status.Should().Be(200);
        body.Should().Be("{\"RxOptP");
    }

    [Fact]
    public void IsRawResponseComplete_ChunkedWithTerminator_IsComplete()
    {
        NokiaXs010xOntProvider.IsRawResponseComplete(
            System.Text.Encoding.ASCII.GetBytes(ChunkedDeviceResponse)).Should().BeTrue();
    }

    [Fact]
    public void IsRawResponseComplete_ChunkedWithoutTerminator_IsIncomplete()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n8\r\n{\"RxOptP\r\n");

        NokiaXs010xOntProvider.IsRawResponseComplete(raw).Should().BeFalse();
    }

    [Fact]
    public void IsRawResponseComplete_NoLengthFraming_WaitsForConnectionClose()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\nbody");

        NokiaXs010xOntProvider.IsRawResponseComplete(raw).Should().BeFalse();
    }

    [Fact]
    public async Task PollAsync_UserAgentGatedFirmware_FallsBackToCurlReplay()
    {
        // Simulates the picky firmware: login works for any client, but getUpdateinfo 401s
        // unless the request carries a User-Agent (the trait every proven-working client - curl
        // and browsers - shares and the shipped HttpClient request lacks). The Direct flow must
        // probe first and fail, then the raw-socket curl replay must complete the walk and read.
        await using var server = new PickyGponFormServer();
        var provider = new NokiaXs010xOntProvider(NullLogger<NokiaXs010xOntProvider>.Instance);
        var context = new OntPollContext
        {
            Id = 1,
            Name = "TestOnt",
            Host = "127.0.0.1",
            Port = server.Port,
            Username = "admin",
            Password = "1234",
        };

        var stats = (await provider.PollAsync(context)).Stats;

        stats.Should().NotBeNull();
        stats!.RxPowerDbm.Should().BeApproximately(-13.3, 0.0001);
        stats.VendorSn.Should().Be("ALCLaabbccdd");
        server.RejectedUserAgentlessUpdateInfo.Should().BeTrue("the Direct flow should have probed first and been 401'd");
    }

    [Fact]
    public async Task PollAsync_MalformedSetCookieFirmware_DirectFlowStillAuthenticates()
    {
        // Simulates the root cause @jakerobb isolated on #929: the firmware answers the login
        // calls with a malformed "Set-Cookie: Path=/; HttpOnly" header (no name=value). With a
        // cookie-enabled handler the CookieContainer swallows it and substitutes garbage for the
        // hand-set sessionid header; with UseCookies off the Direct flow must succeed as-is,
        // without ever falling back to the curl replay.
        await using var server = new PickyGponFormServer(requireUserAgent: false, emitMalformedSetCookie: true);
        var provider = new NokiaXs010xOntProvider(NullLogger<NokiaXs010xOntProvider>.Instance);
        var context = new OntPollContext
        {
            Id = 2,
            Name = "TestOnt",
            Host = "127.0.0.1",
            Port = server.Port,
            Username = "admin",
            Password = "1234",
        };

        var stats = (await provider.PollAsync(context)).Stats;

        stats.Should().NotBeNull();
        stats!.RxPowerDbm.Should().BeApproximately(-13.3, 0.0001);
        server.SawWalkPage.Should().BeFalse("Direct should authenticate without the curl-replay fallback");
    }

    /// <summary>
    /// Minimal GponForm device double that mimics the picky firmware variants: serves the login
    /// handshake to anyone, but 401s getUpdateinfo when the session cookie is wrong (or, when
    /// <c>requireUserAgent</c> is set, when the request has no User-Agent header), and can emit
    /// the malformed Set-Cookie header the real units send on login responses. One request per
    /// connection, Connection: close, like the device.
    /// </summary>
    private sealed class PickyGponFormServer : IAsyncDisposable
    {
        private const string Nonce = "AbCdEfGhIjKlMnOpQrStUvWxYz012345";
        private const string Salt = "ea";
        private const string CookieId = "deadbeefcafe0123";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly bool _requireUserAgent;
        private readonly bool _emitMalformedSetCookie;

        public int Port { get; }

        public bool RejectedUserAgentlessUpdateInfo { get; private set; }

        public bool SawWalkPage { get; private set; }

        public PickyGponFormServer(bool requireUserAgent = true, bool emitMalformedSetCookie = false)
        {
            _requireUserAgent = requireUserAgent;
            _emitMalformedSetCookie = emitMalformedSetCookie;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
        }

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
        }

        private async Task HandleAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var buffer = new byte[16384];
            var received = new MemoryStream();
            string request;
            int headerEnd;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                    return;
                received.Write(buffer, 0, read);
                request = Encoding.ASCII.GetString(received.ToArray());
                headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                    continue;
                if (request.Length >= headerEnd + 4 + GetContentLength(request[..headerEnd]))
                    break;
            }

            var lines = request[..headerEnd].Split("\r\n");
            var target = lines[0].Split(' ')[1];
            var body = request[(headerEnd + 4)..];
            var hasUserAgent = lines.Any(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase));
            var hasSession = lines.Any(l =>
                l.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) && l.Contains($"sessionid={CookieId}"));

            if (target == "/ponpasswd.html")
                SawWalkPage = true;

            var status = "200 OK";
            var setCookie = "";
            string responseBody;
            switch (target)
            {
                case "/GponForm/Login_GetConfig":
                    responseBody = $$"""{"XError":0,"nonce":"{{Nonce}}","saltval":"{{Salt}}"}""";
                    if (_emitMalformedSetCookie)
                        setCookie = "Set-Cookie: Path=/; HttpOnly\r\n";
                    break;
                case "/GponForm/LoginForm":
                    var expectedCmt = NokiaXs010xOntProvider.ComputeCmt("admin", Salt, "1234");
                    responseBody = body.Contains($"cmt={expectedCmt}") && body.Contains($"nonce={Nonce}")
                        ? $$"""{"login_result":"success","cookieid":"{{CookieId}}"}"""
                        : """{"login_result":"error"}""";
                    if (_emitMalformedSetCookie)
                        setCookie = "Set-Cookie: Path=/; HttpOnly\r\n";
                    break;
                case "/GponForm/getUpdateinfo":
                    if ((hasUserAgent || !_requireUserAgent) && hasSession)
                    {
                        responseBody = FullUpdateInfoJson;
                    }
                    else
                    {
                        if (!hasUserAgent)
                            RejectedUserAgentlessUpdateInfo = true;
                        status = "401 Unauthorized";
                        responseBody = "<html>login</html>";
                    }
                    break;
                default:
                    responseBody = "<html></html>";
                    break;
            }

            var response =
                $"HTTP/1.1 {status}\r\nContent-Type: text/html\r\n{setCookie}" +
                $"Content-Length: {Encoding.ASCII.GetByteCount(responseBody)}\r\nConnection: close\r\n\r\n" +
                responseBody;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);
        }

        private static int GetContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line["Content-Length:".Length..].Trim(), out var length))
                    return length;
            }

            return 0;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop; } catch { }
            _cts.Dispose();
        }
    }

    [Fact]
    public void ParseCookieId_SuccessResponse_ReturnsCookie()
    {
        var json = """{"login_result":"success","cookieid":"deadbeefcafe0123"}""";

        NokiaXs010xOntProvider.ParseCookieId(json).Should().Be("deadbeefcafe0123");
    }

    [Fact]
    public void ParseCookieId_ErrorResponse_ReturnsNull()
    {
        NokiaXs010xOntProvider.ParseCookieId("""{"login_result":"error"}""").Should().BeNull();
    }

    [Fact]
    public void ParseCookieId_MissingCookie_ReturnsNull()
    {
        NokiaXs010xOntProvider.ParseCookieId("""{"login_result":"success"}""").Should().BeNull();
    }
}
