using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

/// <summary>
/// The Hitron CODA /1/Device/CM API, against payloads captured from a CODA-5719 (Mediacom),
/// and its PHPSESSID sign-in against a loopback modem.
/// </summary>
public class HitronCodaProviderTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "CableModem", "Fixtures");

    private static string FixtureText(string name) =>
        File.ReadAllText(Path.Combine(FixtureDir, $"hitron-coda5719-{name}.json"));

    private static JsonElement Fixture(string name) => Json(FixtureText(name));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static HitronCodaProvider Provider() => new(NullLogger<HitronCodaProvider>.Instance);

    private static CmPollContext Context(IDeviceDialer? dialer = null) => new()
    {
        Id = 1,
        SiteSlug = "test-site",
        Name = "Cable Modem",
        Host = "192.0.2.10",
        Password = "test-password",
        Dialer = dialer ?? DirectDeviceDialer.Instance,
    };

    private static CableModemStats ParseCaptured() => HitronCodaProvider.Parse(
        Fixture("dsinfo"), Fixture("usinfo"), Fixture("dsofdm"), Fixture("usofdm"),
        Fixture("version"), Fixture("log"), Context());

    // ---- Parsing ----

    [Fact]
    public void Parse_ReadsEveryChannelOfTheCapture()
    {
        var stats = ParseCaptured();

        // 16 SC-QAM + 2 OFDM downstream; 1 SC-QAM + 1 operating OFDMA upstream (the second is DISABLED).
        stats.DownstreamChannels.Should().HaveCount(18);
        stats.UpstreamChannels.Should().HaveCount(2);
        stats.LockedDsChannels.Should().Be(18);
        stats.LockedUsChannels.Should().Be(2);
        stats.DeviceModel.Should().Be("Hitron CODA-5719");
        stats.FirmwareVersion.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Parse_ScQamDownstreamKeepsTheModemsValues()
    {
        var channel = ParseCaptured().DownstreamChannels.Single(c => c.ChannelId == 6 && c.Modulation != "OFDM");

        channel.Modulation.Should().Be("QAM256");
        channel.Frequency.Should().Be(195_000_000);
        channel.Power.Should().Be(-0.9);
        channel.Snr.Should().Be(30.42);
        channel.Correctables.Should().Be(1587932);
        channel.Uncorrectables.Should().Be(319850);
    }

    [Fact]
    public void Parse_OfdmDownstreamSitsAtTheMiddleOfItsOccupiedBand()
    {
        var ofdm = ParseCaptured().DownstreamChannels.Where(c => c.Modulation == "OFDM").ToList();

        ofdm.Should().HaveCount(2);
        var first = ofdm.Single(c => c.ChannelId == 0);
        first.Frequency.Should().Be(343_475_000); // "261 ~ 425.95" MHz
        first.Power.Should().BeApproximately(-1.3, 0.0001);
        first.Snr.Should().BeNull();
        first.Correctables.Should().Be(0);
        first.Uncorrectables.Should().Be(0);
    }

    [Fact]
    public void Parse_FecTotalsCoverScQamOnly()
    {
        var stats = ParseCaptured();
        var scQam = stats.DownstreamChannels.Where(c => c.Modulation != "OFDM").ToList();

        stats.TotalCorrectables.Should().Be(scQam.Sum(c => c.Correctables));
        stats.TotalUncorrectables.Should().Be(scQam.Sum(c => c.Uncorrectables));
        stats.ChannelsWithUncorrectables.Should().Be(16);
    }

    [Fact]
    public void Parse_UpstreamScQamAndOfdma()
    {
        var us = ParseCaptured().UpstreamChannels;

        var scQam = us.Single(c => c.ChannelType == "SC-QAM");
        scQam.ChannelId.Should().Be(1);
        scQam.Frequency.Should().Be(34_700_000);
        scQam.Modulation.Should().Be("QAM64");
        scQam.Power.Should().Be(50.77);
        scQam.SymbolRate.Should().Be(5_120_000);

        var ofdma = us.Single(c => c.ChannelType == "OFDMA");
        ofdma.LockStatus.Should().Be("Locked");
        ofdma.Power.Should().Be(47.0); // repPower1_6, not the 58.383 channel total
        ofdma.Frequency.Should().Be(0);
    }

    [Fact]
    public void Parse_SkipsUnassignedSlots()
    {
        var stats = HitronCodaProvider.Parse(
            Json("""{"errCode":"000","Freq_List":[{"portId":"1","frequency":"0","channelId":"0"}]}"""),
            Json("""{"errCode":"000","Freq_List":[{"portId":"2","frequency":"0","channelId":"0","signalStrength":"0.000"}]}"""),
            Json("""{"errCode":"000","OFDMs_List":[{"receive":1,"plclock":"NO","ncplock":"NO","mdc1lock":"NO","occupiedbw":"","plcpower":" 0.000000"}]}"""),
            null, null, null, Context());

        stats.DownstreamChannels.Should().BeEmpty();
        stats.UpstreamChannels.Should().BeEmpty();
        stats.DeviceModel.Should().Be("Hitron CODA");
        stats.Events.Should().BeEmpty();
    }

    [Fact]
    public void Parse_OfdmThatLostLockIsKeptAsNotLocked()
    {
        var stats = HitronCodaProvider.Parse(
            Json("""{"Freq_List":[]}"""), Json("""{"Freq_List":[]}"""),
            Json("""{"OFDMs_List":[{"receive":0,"plclock":"YES","ncplock":"NO","mdc1lock":"NO","occupiedbw":"261 ~ 425.95","plcpower":" -1.3"}]}"""),
            null, null, null, Context());

        stats.DownstreamChannels.Should().ContainSingle().Which.LockStatus.Should().Be("Not Locked");
        stats.LockedDsChannels.Should().Be(0);
    }

    [Theory]
    [InlineData("QAM256", "QAM256")]
    [InlineData("64QAM", "QAM64")]
    [InlineData("256 QAM", "QAM256")]
    [InlineData("", "")]
    public void FormatModulation_UsesTheSharedSpelling(string raw, string expected) =>
        HitronCodaProvider.FormatModulation(raw).Should().Be(expected);

    // ---- Event log ----

    [Fact]
    public void ParseEventLog_ReadsLevelsTimesAndTextOldestFirst()
    {
        var events = HitronCodaProvider.ParseEventLog(Fixture("log"));

        events.Should().NotBeEmpty();
        events.Select(e => e.Time).Should().BeInAscendingOrder();

        var first = events[0];
        first.Time.Should().Be(new DateTime(2026, 11, 1, 22, 27, 27, DateTimeKind.Utc));
        first.Level.Should().Be("Warning");
        first.Text.Should().Be("RNG-RSP CCAP Commanded Power in Excess of 6 dB Below the Value Corresponding to the Top of the DRW");
        first.Raw.Should().StartWith("2026-11-01T17:27:27-0500 RNG-RSP");

        events.Should().Contain(e => e.Level == "Notice" && e.Text.StartsWith("CM-STATUS message sent."));
        events.Should().OnlyContain(e => !e.Text.Contains("CM-MAC"));
    }

    [Fact]
    public void ParseEventLog_RecognisesT3AndDropsUnsyncedTimes()
    {
        var events = HitronCodaProvider.ParseEventLog(Json("""
            {"Log_List":[
              {"index":2,"time":"2026-11-02T08:00:03-0500","type":"82000400","priority":"3",
               "event":"No Ranging Response received - T3 time-out;CM-MAC=aa:bb:cc:dd:ee:ff;CMTS-MAC=00:11:22:33:44:55;CM-QOS=1.1;CM-VER=3.1;"},
              {"index":1,"time":"1970-01-01T00:00:12+0000","type":"82000200","priority":"3",
               "event":"No Ranging Response received - T3 time-out;CM-MAC=aa:bb:cc:dd:ee:ff;"}
            ]}
            """));

        events.Should().HaveCount(2);
        events[0].Time.Should().BeNull();
        events[1].Time.Should().Be(new DateTime(2026, 11, 2, 13, 0, 3, DateTimeKind.Utc));
        events[1].Level.Should().Be("Critical");
        events[1].Kind.Should().Be(CmEventKinds.T3Timeout);
        events[1].Text.Should().Be("No Ranging Response received - T3 time-out");
    }

    // ---- Sign-in and session ----

    [Fact]
    public async Task Poll_SignsInAsCusadminWhenTheUsernameIsBlank_ThenReusesTheSession()
    {
        await using var device = new TestDevice();
        var provider = Provider();

        var first = await provider.PollAsync(Context(device));
        var second = await provider.PollAsync(Context(device));

        first.Success.Should().BeTrue(first.FailureReason);
        second.Success.Should().BeTrue(second.FailureReason);
        first.Stats!.DownstreamChannels.Should().HaveCount(18);

        var login = device.Requests.Where(r => r.Path == "/1/Device/Users/Login").Should().ContainSingle().Subject;
        login.Method.Should().Be("POST");
        var model = JsonDocument.Parse(login.Form["model"]).RootElement;
        model.GetProperty("username").GetString().Should().Be("cusadmin");
        model.GetProperty("password").GetString().Should().Be("test-password");

        device.Requests.Where(r => r.Path.StartsWith("/1/Device/CM/"))
            .Should().OnlyContain(r => r.Cookie.Contains("PHPSESSID=") && r.XRequestedWith == "XMLHttpRequest");
        device.Opens.Should().OnlyContain(o => o.Host == "192.0.2.10" && o.Port == 80);
    }

    [Fact]
    public async Task Poll_SignsInAgainWhenTheModemDroppedTheSession()
    {
        await using var device = new TestDevice();
        var provider = Provider();

        (await provider.PollAsync(Context(device))).Success.Should().BeTrue();
        device.ExpireSessions();
        var result = await provider.PollAsync(Context(device));

        result.Success.Should().BeTrue(result.FailureReason);
        result.Stats!.DownstreamChannels.Should().HaveCount(18);
        device.Requests.Count(r => r.Path == "/1/Device/Users/Login").Should().Be(2);
    }

    [Fact]
    public async Task Poll_NewCredentialsStartANewSession()
    {
        await using var device = new TestDevice();
        var provider = Provider();

        (await provider.PollAsync(Context(device))).Success.Should().BeTrue();
        (await provider.PollAsync(Context(device) with { Username = "other-user" })).Success.Should().BeTrue();

        var logins = device.Requests.Where(r => r.Path == "/1/Device/Users/Login")
            .Select(r => JsonDocument.Parse(r.Form["model"]).RootElement.GetProperty("username").GetString());
        logins.Should().Equal("cusadmin", "other-user");
    }

    [Fact]
    public async Task Poll_ReportsARejectedSignIn_AndHoldsScheduledRetries()
    {
        await using var device = new TestDevice { Password = "other" };
        var provider = Provider();
        const string rejected = "The Hitron modem rejected the sign-in (Incorrect password). Check the username and password.";

        var first = await provider.PollAsync(Context(device));
        var second = await provider.PollAsync(Context(device));

        first.FailureReason.Should().Be(rejected);
        second.FailureReason.Should().Be(rejected);
        // The second poll does not try the refused credentials again.
        device.Requests.Count(r => r.Path == "/1/Device/Users/Login").Should().Be(1);
    }

    [Fact]
    public async Task RejectedSignInHold_IsLiftedByTheTestButtonAndByNewCredentials()
    {
        await using var device = new TestDevice { Password = "right-password" };
        var provider = Provider();

        (await provider.PollAsync(Context(device))).Success.Should().BeFalse();
        var (tested, _) = await provider.TestConnectionAsync(Context(device));
        tested.Should().BeFalse();
        device.Requests.Count(r => r.Path == "/1/Device/Users/Login").Should().Be(2);

        var fixedPassword = await provider.PollAsync(Context(device) with { Password = "right-password" });
        fixedPassword.Success.Should().BeTrue(fixedPassword.FailureReason);
    }

    [Fact]
    public void BlankUsername_IsTheIspAccount()
    {
        ICableModemProvider provider = Provider();
        provider.BlankUsername.Should().Be("cusadmin");
    }

    [Fact]
    public async Task Poll_WithoutAPasswordFailsBeforeAnyRequest()
    {
        await using var device = new TestDevice();

        var result = await Provider().PollAsync(Context(device) with { Password = "" });

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("Enter the Hitron modem's password in Settings.");
        device.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Poll_MissingOfdmAndLogStillReturnsTheScQamChannels()
    {
        await using var device = new TestDevice { Missing = { "/1/Device/CM/DsOfdm", "/1/Device/CM/UsOfdm", "/1/Device/CM/Log", "/1/Device/CM/Version" } };

        var result = await Provider().PollAsync(Context(device));

        result.Success.Should().BeTrue(result.FailureReason);
        result.Stats!.DownstreamChannels.Should().HaveCount(16);
        result.Stats.UpstreamChannels.Should().HaveCount(1);
        result.Stats.DeviceModel.Should().Be("Hitron CODA");
        result.Stats.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnection_ReportsTheModelAndChannelCounts()
    {
        await using var device = new TestDevice();

        var (success, message) = await Provider().TestConnectionAsync(Context(device));

        success.Should().BeTrue(message);
        message.Should().Be("Connected to Hitron CODA-5719 - 18 downstream, 2 upstream channels detected");
    }

    private sealed record Request(string Method, string Path, string Cookie, string XRequestedWith, Dictionary<string, string> Form);

    /// <summary>
    /// Loopback CODA web server: hands out PHPSESSID on the login page, binds it on a good sign-in,
    /// and answers the API with the captured payloads only for a signed-in cookie.
    /// </summary>
    private sealed class TestDevice : IDeviceDialer, IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _connections = new();
        private readonly ConcurrentDictionary<string, bool> _signedIn = new();
        private int _nextSession;

        public string Password { get; init; } = "test-password";
        public HashSet<string> Missing { get; } = new();
        public ConcurrentQueue<Request> Requests { get; } = new();
        public ConcurrentQueue<(string Host, int Port)> Opens { get; } = new();

        public TestDevice()
        {
            _listener.Start();
            _accept = AcceptAsync();
        }

        public void ExpireSessions() => _signedIn.Clear();

        public async ValueTask<Stream> DialAsync(string host, int port, CancellationToken ct)
        {
            Opens.Enqueue((host, port));
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, ct);
                return client.GetStream();
            }
            catch { client.Dispose(); throw; }
        }

        public Task<(string Host, int Port)> ResolveAsync(string host, int port) => throw new NotSupportedException();

        private (int Status, string Body, string? SetCookie) Handle(Request request)
        {
            var path = request.Path.Split('?')[0];
            var session = request.Cookie.Split(';', StringSplitOptions.TrimEntries)
                .FirstOrDefault(c => c.StartsWith("PHPSESSID=", StringComparison.Ordinal))?["PHPSESSID=".Length..];

            if (path == "/webpages/login.html")
                return (200, "<html></html>", $"PHPSESSID=s{Interlocked.Increment(ref _nextSession)}; path=/; HttpOnly");

            if (path == "/1/Device/Users/Login")
            {
                var model = JsonDocument.Parse(request.Form["model"]).RootElement;
                if (session == null || model.GetProperty("password").GetString() != Password)
                    return (200, """{"errCode":"001","errMsg":"Incorrect password","result":"fail"}""", null);
                // The modem issues a fresh cookie on sign-in.
                var issued = $"s{Interlocked.Increment(ref _nextSession)}";
                _signedIn[issued] = true;
                return (200, """{"errCode":"000","errMsg":"","result":"success"}""",
                    $"PHPSESSID={issued}; path=/; HttpOnly");
            }

            if (!path.StartsWith("/1/Device/CM/", StringComparison.Ordinal))
                return (404, "", null);
            if (session == null || !_signedIn.ContainsKey(session))
                return (200, "<script>RedirectLoginIdentify</script>", null);
            if (Missing.Contains(path))
                return (404, "", null);

            return path switch
            {
                "/1/Device/CM/DsInfo" => (200, FixtureText("dsinfo"), null),
                "/1/Device/CM/UsInfo" => (200, FixtureText("usinfo"), null),
                "/1/Device/CM/DsOfdm" => (200, FixtureText("dsofdm"), null),
                "/1/Device/CM/UsOfdm" => (200, FixtureText("usofdm"), null),
                "/1/Device/CM/Version" => (200, FixtureText("version"), null),
                "/1/Device/CM/Log" => (200, FixtureText("log"), null),
                _ => (404, "", null),
            };
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    _connections.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (OperationCanceledException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var line = (await reader.ReadLineAsync(_stop.Token))!.Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                    {
                        var separator = header.IndexOf(':');
                        headers[header[..separator]] = header[(separator + 1)..].Trim();
                    }
                    var length = headers.TryGetValue("Content-Length", out var raw) ? int.Parse(raw) : 0;
                    var body = new char[length];
                    if (length > 0) await reader.ReadBlockAsync(body.AsMemory(), _stop.Token);
                    var fields = new Dictionary<string, string>();
                    foreach (var field in new string(body).Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pair = field.Split('=', 2);
                        fields[WebUtility.UrlDecode(pair[0])] = WebUtility.UrlDecode(pair.ElementAtOrDefault(1) ?? "");
                    }

                    var request = new Request(line[0], line[1],
                        headers.GetValueOrDefault("Cookie") ?? "",
                        headers.GetValueOrDefault("X-Requested-With") ?? "",
                        fields);
                    Requests.Enqueue(request);

                    var (status, replyBody, setCookie) = Handle(request);
                    var bytes = Encoding.UTF8.GetBytes(replyBody);
                    var head = $"HTTP/1.1 {status} Test\r\nContent-Type: text/html; charset=UTF-8\r\n" +
                               $"Content-Length: {bytes.Length}\r\nConnection: close\r\n" +
                               (setCookie == null ? "" : $"Set-Cookie: {setCookie}\r\n") + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
