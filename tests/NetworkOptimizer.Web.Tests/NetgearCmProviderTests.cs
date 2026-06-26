using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class NetgearCmProviderTests
{
    private static byte[] Raw(string text) => Encoding.ASCII.GetBytes(text.Replace("\n", "\r\n"));

    [Fact]
    public void ParseRawHttpResponse_ParsesWellFormedResponse()
    {
        var raw = Raw(
            "HTTP/1.1 200 OK\n" +
            "Content-Type: text/html\n" +
            "Content-Length: 5\n" +
            "\n" +
            "hello");

        var resp = NetgearCmProvider.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers["Content-Type"].Should().Be("text/html");
        resp.Body.Should().Be("hello");
    }

    // The CM700's NET-DK/1.0 server intermittently drops the leading bytes of a header line. The
    // HAR for issue #869 shows "Content-Length" arriving as "ntent-Length" (the "Co" dropped).
    // That line still has a colon, so the lenient reader keeps it (as an unrecognized header) and,
    // with no usable Content-Length, returns the whole body that arrived before the connection
    // closed. .NET's strict parser would not choke on this colon-bearing line, but the reader must
    // still produce the right body without a real Content-Length.
    [Fact]
    public void ParseRawHttpResponse_ToleratesDroppedHeaderNameBytes()
    {
        var raw = Raw(
            "HTTP/1.1 200 OK\n" +
            "Content-Type: text/html\n" +
            "ntent-Length: 50471\n" +
            "Connection: close\n" +
            "\n" +
            "<html>dsTable</html>");

        var resp = NetgearCmProvider.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers.Should().NotContainKey("Content-Length");
        // No usable Content-Length, so the body is everything after the header separator.
        resp.Body.Should().Be("<html>dsTable</html>");
    }

    // The reporter's log showed `Received an invalid header line: 'ceived response error'.` - the
    // same byte-drop landing on a line that lost its colon entirely ("Received response error"
    // minus "Re"). .NET aborts the whole response on such a line; the lenient reader skips it and
    // still returns the status and body.
    [Fact]
    public void ParseRawHttpResponse_SkipsColonlessCorruptedLine()
    {
        var raw = Raw(
            "HTTP/1.1 200 OK\n" +
            "Content-Type: text/html\n" +
            "ceived response error\n" +
            "Connection: close\n" +
            "\n" +
            "<html>dsTable</html>");

        var resp = NetgearCmProvider.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers["Content-Type"].Should().Be("text/html");
        resp.Body.Should().Be("<html>dsTable</html>");
    }

    [Fact]
    public void ParseRawHttpResponse_RecoversStatusFromClippedStatusLine()
    {
        // Leading bytes dropped from the status line too: "HTTP/1.1 401 Unauthorized" -> the code
        // is still recovered from the first 3-digit token.
        var raw = Raw(
            "TP/1.1 401 Unauthorized\n" +
            "WWW-Authenticate: Basic realm=\"NETGEAR CM700\"\n" +
            "Set-Cookie: XSRF_TOKEN=2104060059; Path=/\n" +
            "\n");

        var resp = NetgearCmProvider.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(401);
        resp.Headers["Set-Cookie"].Should().Contain("XSRF_TOKEN=2104060059");
    }

    [Fact]
    public void ParseRawHttpResponse_HandlesBareLfLineEndings()
    {
        var raw = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\n" +
            "Content-Type: text/html\n" +
            "\n" +
            "body-bytes");

        var resp = NetgearCmProvider.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers["Content-Type"].Should().Be("text/html");
        resp.Body.Should().Be("body-bytes");
    }

    [Fact]
    public void ParseRawHttpResponse_BoundsBodyByContentLengthWhenValid()
    {
        var raw = Raw(
            "HTTP/1.1 200 OK\n" +
            "Content-Length: 5\n" +
            "\n" +
            "helloEXTRA");

        var resp = NetgearCmProvider.ParseRawHttpResponse(raw, raw.Length);

        resp.Body.Should().Be("hello");
    }

    private static CmPollContext Context => new()
    {
        Id = 1,
        Name = "TestModem",
        Host = "192.168.100.1",
    };

    // Mirrors the CM700 DocsisStatus.htm shape: the dsTable/usTable elements carry only a
    // header row (the example data rows are inside an HTML comment), and the live channel
    // data lives in a JavaScript tagValueList - a leading channel count followed by
    // pipe-delimited per-channel fields. The placeholder assignment in each function is
    // double-quoted and commented out; the live one is single-quoted.
    private const string Cm700JsHtml = """
        <html><head><script>
        function InitUsTableTagValue()
        {
            /* Channel | Lock Status | US Channel Type | Channel ID | Symbol Rate | Frequency | Power */
            /* var tagValueList = "4" + "|1|Not Locked|Unknown|0|0|0|0.0"; */
            var tagValueList = '2|1|Locked|ATDMA|17|5120|16400000 Hz|40.7|2|Locked|ATDMA|18|5120|22800000 Hz|39.4|';
            drawTable('usTable', tagValueList, onAddUsRowCB);
        }
        function InitDsTableTagValue()
        {
            /* Channel | Lock Status | Modulation | Channel ID | Frequency | Power | SNR */
            /* var tagValueList = "8" + "|1|Not Locked|Unknown|0|0|0.0|0.0"; */
            var tagValueList = '3|1|Locked|QAM 256|1|387000000 Hz|5.8|40.9|7|0|2|Locked|QAM 256|2|393000000 Hz|5.8|41.0|0|0|3|Locked|OFDM PLC|159|960000000 Hz|6.0|41.2|1148|12|';
            drawTable('dsTable', tagValueList, onAddDsRowCB);
        }
        </script></head><body>
        <table id="dsTable" class="TableStyle">
          <tr><td><span class="thead">Channel</span></td><td><span class="thead">UnCorrectables</span></td></tr>
        <!--
          <tr><td>1</td><td>Locked</td><td>unknown</td><td>0</td><td>0 Hz</td><td>0.0 dBmV</td><td>0.0 dB</td></tr>
        -->
        </table>
        <table id="usTable" class="TableStyle">
          <tr><td><span class="thead">Channel</span></td></tr>
        </table>
        </body></html>
        """;

    // Server-rendered .asp shape (CM600/CM1000): full downstream table including the FEC
    // counters. Guards the HtmlAgilityPack path and confirms the JS fallback does not run
    // when the tables already have data.
    private const string ServerRenderedHtml = """
        <table id="dsTable" class="TableStyle">
          <tr>
            <td><span class="thead">Channel</span></td>
            <td><span class="thead">Lock Status</span></td>
            <td><span class="thead">Modulation</span></td>
            <td><span class="thead">Channel ID</span></td>
            <td><span class="thead">Frequency</span></td>
            <td><span class="thead">Power</span></td>
            <td><span class="thead">SNR</span></td>
            <td><span class="thead">Correctables</span></td>
            <td><span class="thead">UnCorrectables</span></td>
          </tr>
          <tr><td>1</td><td>Locked</td><td>256QAM</td><td>1</td><td>591000000 Hz</td><td>-2.5 dBmV</td><td> 40.0 dB</td><td>1234</td><td>5</td></tr>
        </table>
        """;

    [Fact]
    public void ParseDocsisStatus_ParsesDownstreamFromJavaScriptTagValueList()
    {
        var stats = NetgearCmProvider.ParseDocsisStatus(Cm700JsHtml, Context);

        stats.DownstreamChannels.Should().HaveCount(3);

        var first = stats.DownstreamChannels[0];
        first.ChannelId.Should().Be(1);
        first.LockStatus.Should().Be("Locked");
        first.Modulation.Should().Be("QAM 256");
        first.Frequency.Should().Be(387000000);
        first.Power.Should().Be(5.8);
        first.Snr.Should().Be(40.9);
        first.Correctables.Should().Be(7);
        first.Uncorrectables.Should().Be(0);

        // Channel ID column (not the sequential index) wins, and the FEC counters carry through.
        var third = stats.DownstreamChannels[2];
        third.ChannelId.Should().Be(159);
        third.Modulation.Should().Be("OFDM PLC");
        third.Correctables.Should().Be(1148);
        third.Uncorrectables.Should().Be(12);
    }

    [Fact]
    public void ParseDocsisStatus_ParsesUpstreamFromJavaScriptTagValueList()
    {
        var stats = NetgearCmProvider.ParseDocsisStatus(Cm700JsHtml, Context);

        stats.UpstreamChannels.Should().HaveCount(2);

        var first = stats.UpstreamChannels[0];
        first.ChannelId.Should().Be(17);
        first.LockStatus.Should().Be("Locked");
        first.ChannelType.Should().Be("ATDMA");
        first.SymbolRate.Should().Be(5120);
        first.Frequency.Should().Be(16400000);
        first.Power.Should().Be(40.7);
    }

    [Fact]
    public void ParseDocsisStatus_IgnoresCommentedPlaceholderRows()
    {
        // The only non-JS rows in the CM700 page are commented out; parsing must come solely
        // from the tagValueList, not the 0-valued placeholder row.
        var stats = NetgearCmProvider.ParseDocsisStatus(Cm700JsHtml, Context);

        stats.DownstreamChannels.Should().OnlyContain(c => c.LockStatus == "Locked");
        stats.DownstreamChannels.Should().Contain(c => c.Frequency == 387000000);
    }

    // Mixed lock states (as the real CM700 capture has 3 "Not Locked" upstream channels with
    // 0.0 power): the aggregates written to InfluxDB must count and average only locked
    // channels, so unlocked 0-value channels don't drag the averages down.
    private const string Cm700MixedLockHtml = """
        <html><head><script>
        function InitUsTableTagValue()
        {
            var tagValueList = '2|1|Locked|ATDMA|17|5120|16400000 Hz|45.0|2|Not Locked|N/A|Unknown|0|0 Hz|0.0|';
        }
        function InitDsTableTagValue()
        {
            var tagValueList = '2|1|Locked|QAM 256|1|387000000 Hz|5.0|40.0|10|2|2|Not Locked|Unknown|0|0 Hz|0.0|0.0|0|0|';
        }
        </script></head><body>
        <table id="dsTable"><tr><td><span class="thead">Channel</span></td></tr></table>
        <table id="usTable"><tr><td><span class="thead">Channel</span></td></tr></table>
        </body></html>
        """;

    [Fact]
    public void ParseDocsisStatus_AggregatesOnlyLockedChannels()
    {
        var stats = NetgearCmProvider.ParseDocsisStatus(Cm700MixedLockHtml, Context);

        // Both channels are parsed and cached, but only the locked ones count toward the
        // metrics written to InfluxDB.
        stats.DownstreamChannels.Should().HaveCount(2);
        stats.UpstreamChannels.Should().HaveCount(2);

        stats.LockedDsChannels.Should().Be(1);
        stats.LockedUsChannels.Should().Be(1);

        // Averages exclude the unlocked 0.0-value channels rather than being pulled toward 0.
        stats.DownstreamPowerAvgDbmv.Should().Be(5.0);
        stats.DownstreamSnrAvgDb.Should().Be(40.0);
        stats.UpstreamPowerAvgDbmv.Should().Be(45.0);

        // FEC counters sum across the downstream channels.
        stats.TotalCorrectables.Should().Be(10);
        stats.TotalUncorrectables.Should().Be(2);
        stats.ChannelsWithUncorrectables.Should().Be(1);
    }

    [Fact]
    public void ParseDocsisStatus_StillParsesServerRenderedTables()
    {
        var stats = NetgearCmProvider.ParseDocsisStatus(ServerRenderedHtml, Context);

        var ds = stats.DownstreamChannels.Should().ContainSingle().Subject;
        ds.ChannelId.Should().Be(1);
        ds.Modulation.Should().Be("256QAM");
        ds.Power.Should().Be(-2.5);
        ds.Snr.Should().Be(40.0);
        ds.Correctables.Should().Be(1234);
        ds.Uncorrectables.Should().Be(5);
    }

    // The CM2050V's DocsisStatus.htm carries FOUR tagValueList tables: SC-QAM + OFDM downstream
    // and ATDMA + OFDMA upstream. Values are taken verbatim from a real CM2050V capture (trimmed
    // to a couple of channels per table; the leading count matches). The unused upstream slots
    // arrive as "Not Locked" placeholders, which - per the existing convention - are kept in the
    // lists and excluded from the locked-only aggregates.
    private const string Cm2050VHtml = """
        <html><head><script>
        function InitDsTableTagValue()
        {
            /* var tagValueList = "8" + "|1|Not Locked|Unknown|0|0|0.0|0.0|0|0"; */
            var tagValueList = '2|1|Locked|QAM256|12|459000000 Hz|8.7|44.1|336|22|2|Locked|QAM256|1|393000000 Hz|8|43.9|445|1004|';
            drawTable('dsTable', tagValueList, onAddDsRowCB);
        }
        function InitUsTableTagValue()
        {
            var tagValueList = '2|1|Locked|ATDMA|1|5120 Ksym/sec|16400000 Hz|39.5 dBmV|2|Not Locked|Unknown|0|0|0|0.0|';
            drawTable('usTable', tagValueList, onAddUsRowCB);
        }
        function InitDsOfdmTableTagValue()
        {
            var tagValueList = '1|1|Locked|0 ,1 ,2 ,3|193|690000000 Hz|8.98 dBmV|43.4 dB|508 ~ 3587|80153994898|63946208630|3923|';
            drawTable('dsOfdmTable', tagValueList, onAddDsOfdmRowCB);
        }
        function InitUsOfdmaTableTagValue()
        {
            var tagValueList = '2|1|Locked|12 ,13|41|36200000 Hz|37.5 dBmV|2|Not Locked|0|0|0 Hz|0 dBmV';
            drawTable('usOfdmaTable', tagValueList, onAddUsOfdmaRowCB);
        }
        </script></head><body>
        <table id="dsTable"><tr><td><span class="thead">Channel</span></td></tr></table>
        <table id="usTable"><tr><td><span class="thead">Channel</span></td></tr></table>
        </body></html>
        """;

    [Fact]
    public void ParseDocsisStatus_ParsesCm2050VScQamAndOfdmDownstream()
    {
        var stats = NetgearCmProvider.ParseDocsisStatus(Cm2050VHtml, Context);

        // 2 SC-QAM + 1 OFDM downstream channels.
        stats.DownstreamChannels.Should().HaveCount(3);

        var scqam = stats.DownstreamChannels[0];
        scqam.ChannelId.Should().Be(12);
        scqam.LockStatus.Should().Be("Locked");
        scqam.Modulation.Should().Be("QAM256");
        scqam.Frequency.Should().Be(459000000);
        scqam.Power.Should().Be(8.7);
        scqam.Snr.Should().Be(44.1);
        scqam.Correctables.Should().Be(336);
        scqam.Uncorrectables.Should().Be(22);

        var ofdm = stats.DownstreamChannels.Single(c => c.ChannelId == 193);
        ofdm.Modulation.Should().Be("OFDM");
        ofdm.Frequency.Should().Be(690000000);
        ofdm.Power.Should().Be(8.98);
        ofdm.Snr.Should().Be(43.4);
        // OFDM codeword counts (billions) are intentionally left out of the FEC aggregates.
        ofdm.Correctables.Should().Be(0);
        ofdm.Uncorrectables.Should().Be(0);
    }

    [Fact]
    public void ParseDocsisStatus_ParsesCm2050VAtdmaAndOfdmaUpstream()
    {
        var stats = NetgearCmProvider.ParseDocsisStatus(Cm2050VHtml, Context);

        // 2 ATDMA (one a placeholder) + 2 OFDMA (one a placeholder) = 4 entries kept.
        stats.UpstreamChannels.Should().HaveCount(4);

        var atdma = stats.UpstreamChannels[0];
        atdma.ChannelId.Should().Be(1);
        atdma.LockStatus.Should().Be("Locked");
        atdma.ChannelType.Should().Be("ATDMA");
        atdma.SymbolRate.Should().Be(5120);
        atdma.Frequency.Should().Be(16400000);
        atdma.Power.Should().Be(39.5);

        var ofdma = stats.UpstreamChannels.Single(c => c.ChannelId == 41);
        ofdma.LockStatus.Should().Be("Locked");
        ofdma.ChannelType.Should().Be("OFDMA");
        ofdma.Frequency.Should().Be(36200000);
        ofdma.Power.Should().Be(37.5);
        ofdma.SymbolRate.Should().Be(0);
    }

    [Fact]
    public void ParseDocsisStatus_Cm2050VAggregatesAcrossAllFourTables()
    {
        var stats = NetgearCmProvider.ParseDocsisStatus(Cm2050VHtml, Context);

        // All three downstream channels are locked; only the real upstream channels are.
        stats.LockedDsChannels.Should().Be(3);
        stats.LockedUsChannels.Should().Be(2);

        // Averages span SC-QAM + OFDM downstream and ATDMA + OFDMA upstream, locked only.
        stats.DownstreamPowerAvgDbmv.Should().BeApproximately((8.7 + 8 + 8.98) / 3, 0.0001);
        stats.DownstreamSnrAvgDb.Should().BeApproximately((44.1 + 43.9 + 43.4) / 3, 0.0001);
        stats.UpstreamPowerAvgDbmv.Should().BeApproximately((39.5 + 37.5) / 2, 0.0001);

        // FEC totals come from the SC-QAM channels only (OFDM counts excluded).
        stats.TotalCorrectables.Should().Be(336 + 445);
        stats.TotalUncorrectables.Should().Be(22 + 1004);
    }

    // ----- Raw-socket fallback (FetchViaRawSocketAsync) -----
    // These drive the lenient raw fetch end to end against a loopback "fake NET-DK modem" so the
    // two-step Basic + anti-CSRF cookie handshake, the broadened retry gate, and the header-scoped
    // cookie extraction are all exercised on real sockets.

    // A status page minimal enough to round-trip: it contains the dsTable token (so it reads as a
    // real status page) and a parseable tagValueList.
    private const string RawStatusPage =
        "<html><body><table id=\"dsTable\"></table><script>function InitDsTableTagValue(){ " +
        "var tagValueList = '1|1|Locked|QAM 256|1|387000000 Hz|5.8|40.9|7|0|'; }</script></body></html>";

    private static byte[] RawResponse(string statusLine, IEnumerable<string> headerLines, string body)
    {
        var sb = new StringBuilder();
        sb.Append(statusLine).Append("\r\n");
        foreach (var h in headerLines)
            sb.Append(h).Append("\r\n");
        sb.Append("\r\n").Append(body);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public async Task FetchViaRawSocketAsync_PrimesCookieThenReturnsPage()
    {
        using var server = new FakeNetDkServer(
            RawResponse("HTTP/1.0 401 Unauthorized",
                new[] { "WWW-Authenticate: Basic realm=\"NETGEAR CM700\"", "Set-Cookie: XSRF_TOKEN=primed123; Path=/", "Connection: close" },
                "<html>401</html>"),
            RawResponse("HTTP/1.0 200 OK",
                new[] { "Content-Type: text/html", "Connection: close" },
                RawStatusPage));

        var provider = new NetgearCmProvider(NullLogger<NetgearCmProvider>.Instance);
        var body = await provider.FetchViaRawSocketAsync(
            $"http://127.0.0.1:{server.Port}/DocsisStatus.htm", "admin", "password", CancellationToken.None);

        body.Should().Be(RawStatusPage);
        server.ReceivedRequests.Should().HaveCount(2);
        // First request carries the preemptive Basic header but no cookie yet.
        server.ReceivedRequests[0].Should().Contain("Authorization: Basic");
        server.ReceivedRequests[0].Should().NotContain("Cookie:");
        // Second request replays the primed cookie alongside Basic.
        server.ReceivedRequests[1].Should().Contain("Cookie: XSRF_TOKEN=primed123");
        server.ReceivedRequests[1].Should().Contain("Authorization: Basic");
    }

    [Fact]
    public async Task FetchViaRawSocketAsync_ReadsBodyDespiteCorruptedContentLengthHeader()
    {
        // The 200 carries a corrupted "Content-Length" (the "Co" dropped, as in the HAR). With no
        // usable length the reader falls back to reading until the connection closes.
        using var server = new FakeNetDkServer(
            RawResponse("HTTP/1.0 401 Unauthorized",
                new[] { "Set-Cookie: XSRF_TOKEN=abc; Path=/", "Connection: close" }, "401"),
            RawResponse("HTTP/1.0 200 OK",
                new[] { "Content-Type: text/html", "ntent-Length: 99999", "Connection: close" }, RawStatusPage));

        var provider = new NetgearCmProvider(NullLogger<NetgearCmProvider>.Instance);
        var body = await provider.FetchViaRawSocketAsync(
            $"http://127.0.0.1:{server.Port}/DocsisStatus.htm", "admin", "password", CancellationToken.None);

        body.Should().Be(RawStatusPage);
    }

    [Fact]
    public async Task FetchViaRawSocketAsync_RetriesWhenStatusLineCorruptedButCookiePresent()
    {
        // Hardening #1: the 401's status line was itself mangled past a recoverable code, so it
        // parses as an unknown status. The retry must still fire because the anti-CSRF cookie is
        // present - the broadened gate keys off the cookie, not an exact 401.
        using var server = new FakeNetDkServer(
            RawResponse("orized",
                new[] { "Set-Cookie: XSRF_TOKEN=primed456; Path=/", "Connection: close" },
                "<html>err</html>"),
            RawResponse("HTTP/1.0 200 OK",
                new[] { "Connection: close" },
                RawStatusPage));

        var provider = new NetgearCmProvider(NullLogger<NetgearCmProvider>.Instance);
        var body = await provider.FetchViaRawSocketAsync(
            $"http://127.0.0.1:{server.Port}/DocsisStatus.htm", "admin", "password", CancellationToken.None);

        body.Should().Be(RawStatusPage);
        server.ReceivedRequests.Should().HaveCount(2);
        server.ReceivedRequests[1].Should().Contain("Cookie: XSRF_TOKEN=primed456");
    }

    [Fact]
    public async Task FetchViaRawSocketAsync_IgnoresXsrfTokenInBodyAndDoesNotRetry()
    {
        // Hardening #2: no Set-Cookie header, but a stray XSRF_TOKEN= sits in the page body. The
        // header-scoped search must not pick it up, so there is no cookie, no retry fires, and the
        // 401 surfaces. A single received request proves the body token was ignored.
        using var server = new FakeNetDkServer(
            RawResponse("HTTP/1.0 401 Unauthorized",
                new[] { "WWW-Authenticate: Basic realm=\"NETGEAR CM700\"", "Connection: close" },
                "<html>var x='XSRF_TOKEN=frombody';</html>"));

        var provider = new NetgearCmProvider(NullLogger<NetgearCmProvider>.Instance);
        Func<Task> act = () => provider.FetchViaRawSocketAsync(
            $"http://127.0.0.1:{server.Port}/DocsisStatus.htm", "admin", "wrongpw", CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        server.ReceivedRequests.Should().HaveCount(1);
    }

    /// <summary>
    /// Minimal loopback HTTP server that serves a fixed list of raw (byte-exact) responses in
    /// order, one per accepted connection, and records each request it received. Lets the
    /// raw-socket fallback be tested end to end - including deliberately malformed responses - on
    /// real sockets. Connections are handled sequentially, matching the provider's one-request-
    /// per-connection (Connection: close) behavior.
    /// </summary>
    private sealed class FakeNetDkServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<byte[]> _responses;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _served;

        public List<string> ReceivedRequests { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeNetDkServer(params byte[][] responses)
        {
            _responses = responses.ToList();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    await HandleAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                using var stream = client.GetStream();

                // Read the request headers (the provider sends a GET with no body) up to the blank line.
                var requestText = new StringBuilder();
                var buf = new byte[1024];
                while (!requestText.ToString().Contains("\r\n\r\n"))
                {
                    var n = await stream.ReadAsync(buf, _cts.Token);
                    if (n == 0)
                        break;
                    requestText.Append(Encoding.ASCII.GetString(buf, 0, n));
                }

                byte[] response;
                lock (_gate)
                {
                    ReceivedRequests.Add(requestText.ToString());
                    response = _responses[Math.Min(_served, _responses.Count - 1)];
                    _served++;
                }

                await stream.WriteAsync(response, _cts.Token);
                await stream.FlushAsync(_cts.Token);
                // Disposing the connection closes the socket, so the client reads to EOF.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); }
            catch { /* shutdown best-effort */ }
            _cts.Dispose();
        }
    }
}
