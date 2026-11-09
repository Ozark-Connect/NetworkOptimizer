using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Tests for <see cref="PeterPonOntProvider"/>. Fixtures are the luci.gpon status, info, and live_get
/// replies from a web UI capture of an FS.com GPON-SFP-ONT-MAC-I on Peter-Pon, with the GPON serial and the service
/// VLANs replaced by generic values.
/// </summary>
public class PeterPonOntProviderTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "OntProviders", "Fixtures");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureDir, name));

    /// <summary>A captured reply re-addressed to <paramref name="id"/>, as the stick answers each call by its id.</summary>
    private static string Reply(string fixture, int id)
    {
        var node = JsonNode.Parse(Fixture(fixture))!;
        node["id"] = id;
        return node.ToJsonString();
    }

    private static string StatusReply(int id = 10) => Reply("peterpon-status.json", id);
    private static string InfoReply(int id = 11) => Reply("peterpon-info.json", id);
    private static string LiveReply(int id = 12) => Reply("peterpon-live-get.json", id);

    private static string Ok(int id, string data) => $$"""{"jsonrpc":"2.0","id":{{id}},"result":[0,{{data}}]}""";

    private static OntPollContext Context(int port = 80, string? username = null, string? password = null) => new()
    {
        Id = 1,
        Name = "Peter-Pon",
        Host = "127.0.0.1",
        Port = port,
        Username = username,
        Password = password,
    };

    private static PeterPonOntProvider NewProvider() => new(NullLogger<PeterPonOntProvider>.Instance);

    // --- Parsing ---

    [Theory]
    [InlineData("12.14uW -19.16dBm", -19.16)]
    [InlineData("1.43mW 1.57dBm", 1.57)]
    [InlineData("1.00mW 0dBm", 0.0)]
    [InlineData("0.12uW -39.2 dBm", -39.2)]
    public void ParseDbm_ReadsTheDbmHalf(string text, double expected)
    {
        PeterPonOntProvider.ParseDbm(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("0.00uW -infdBm")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12.14uW")]
    public void ParseDbm_NoReading_IsNull(string? text)
    {
        PeterPonOntProvider.ParseDbm(text).Should().BeNull();
    }

    [Fact]
    public void KelvinToCelsius_RoundsToOneDecimal()
    {
        PeterPonOntProvider.KelvinToCelsius(330).Should().Be(56.9);
        PeterPonOntProvider.KelvinToCelsius(323).Should().Be(49.9);
        PeterPonOntProvider.KelvinToCelsius(0).Should().BeNull();
        PeterPonOntProvider.KelvinToCelsius(null).Should().BeNull();
    }

    [Fact]
    public void ParseLoginToken_Success_ReturnsSession()
    {
        var json = Ok(1, """{"ubus_rpc_session":"95874178cf877b342cdfd45276cb12bb","timeout":300,"expires":299}""");

        PeterPonOntProvider.ParseLoginToken(json).Should().Be("95874178cf877b342cdfd45276cb12bb");
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":[6]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":-32002,"message":"Access denied"}}""")]
    public void ParseLoginToken_Refused_IsNull(string json)
    {
        PeterPonOntProvider.ParseLoginToken(json).Should().BeNull();
    }

    [Fact]
    public void ParseStatusAndInfo_Fixture_MatchesRepliesById()
    {
        // Out of order on purpose: JSON-RPC does not promise batch order.
        var batch = PeterPonOntProvider.ParseStatusAndInfo($"[{InfoReply()},{StatusReply()}]");

        batch.AccessDenied.Should().BeFalse();
        batch.Status!.PloamState.Should().Be(5);
        batch.Status.RxPower.Should().Be("12.14uW -19.16dBm");
        batch.Status.LaserTempK.Should().Be(323);
        batch.Status.DieTempK.Should().Be(330);
        batch.Info!.GponSn.Should().Be("HWTC12345678");
        batch.Info.OltVendor.Should().Be("HWTC");
        batch.Info.OltVersion.Should().Be("10");
        batch.Info.OltEquipment.Should().BeNull();
    }

    [Fact]
    public void ParseStatusAndInfo_ExpiredSession_ReportsAccessDenied()
    {
        const string denied = """{"jsonrpc":"2.0","id":10,"error":{"code":-32002,"message":"Access denied"}}""";

        var batch = PeterPonOntProvider.ParseStatusAndInfo($"[{denied}]");

        batch.AccessDenied.Should().BeTrue();
        batch.Status.Should().BeNull();
    }

    [Fact]
    public void ParseStatusAndInfo_MissingObject_IsNotAccessDenied()
    {
        // ubus status 4 = UBUS_STATUS_NOT_FOUND: the firmware has no luci.gpon.
        const string missing = """[{"jsonrpc":"2.0","id":10,"result":[4]},{"jsonrpc":"2.0","id":11,"result":[4]}]""";

        var batch = PeterPonOntProvider.ParseStatusAndInfo(missing);

        batch.AccessDenied.Should().BeFalse();
        batch.Status.Should().BeNull();
        batch.Info.Should().BeNull();
    }

    [Fact]
    public void ParseLive_Fixture_TakesNewestSample()
    {
        // The capture's three samples: bias 8680, 8531, 8531 uA; vcc 3349, 3349, 3347 mV.
        var sample = PeterPonOntProvider.ParseLive(LiveReply());

        sample.Should().NotBeNull();
        sample!.Seq.Should().Be(6237);
        sample.BiasMa.Should().Be(8.53);
        sample.VoltageV.Should().Be(3.35);
        sample.UptimeS.Should().Be(134171, "uptime is on the sample clock (updated_s), not the daemon's uptime_s");
    }

    [Fact]
    public void ParseLive_NoUpdatedS_UptimeFromNewestSampleTime()
    {
        var live = JsonNode.Parse(Fixture("peterpon-live-get.json"))!;
        var docs = live["result"]![1]!["docs"]!.AsArray();
        var inner = JsonNode.Parse(docs[0]!.GetValue<string>())!.AsObject();
        inner.Remove("updated_s");
        docs[0] = inner.ToJsonString();

        PeterPonOntProvider.ParseLive(live.ToJsonString())!.UptimeS.Should().Be(134171);
    }

    [Fact]
    public void ParseLive_NoDocs_KeepsSeqOnly()
    {
        var sample = PeterPonOntProvider.ParseLive(Ok(12, """{"seq":42,"docs":[]}"""));

        sample!.Seq.Should().Be(42);
        sample.BiasMa.Should().BeNull();
        sample.Times.Should().BeEmpty();
        sample.Rates.Should().BeEmpty();
    }

    [Fact]
    public void ParseLive_SkipsNonLiveDocuments()
    {
        var slowDoc = new JsonObject { ["tool"] = "live_slow" }.ToJsonString();
        var live = JsonNode.Parse(Fixture("peterpon-live-get.json"))!;
        var docs = live["result"]![1]!["docs"]!.AsArray();
        docs.Insert(0, slowDoc);

        PeterPonOntProvider.ParseLive(live.ToJsonString())!.BiasMa.Should().Be(8.53);
    }

    [Fact]
    public void ParseLive_Refused_IsNull()
    {
        PeterPonOntProvider.ParseLive("""{"jsonrpc":"2.0","id":12,"result":[6]}""").Should().BeNull();
    }

    [Fact]
    public void MapToOntStats_Fixture()
    {
        var batch = PeterPonOntProvider.ParseStatusAndInfo($"[{StatusReply()},{InfoReply()}]");

        var stats = PeterPonOntProvider.MapToOntStats(batch.Status!, batch.Info, Context());

        stats.RxPowerDbm.Should().Be(-19.16);
        stats.TxPowerDbm.Should().Be(1.57);
        stats.TemperatureC.Should().Be(49.9, "the laser temperature is the optic reading DDM reports");
        stats.PonType.Should().Be("GPON");
        stats.PonLinkStatus.Should().Be(PonLinkState.Operation);
        stats.LinkState.Should().Be("Connected (O5)");
        stats.OperationalStatus.Should().Be("Up");
        stats.VendorSn.Should().Be("HWTC12345678");
        stats.OltVendor.Should().Be("HWTC");
        stats.OltModel.Should().BeNull();
        stats.DeviceModel.Should().Be(PeterPonOntProvider.DefaultModel);

        stats.Pon.Should().NotBeNull();
        stats.Pon!.PonLinkStatus.Should().Be("operation");
        stats.Pon.PloamStateRaw.Should().Be(5);
        stats.Pon.OltVendorId.Should().Be("HWTC");
        stats.Pon.OltVersion.Should().Be("10");
        stats.Pon.FirmwareVersion.Should().Be("OpenWrt SNAPSHOT r0-baf6e531");
        stats.Pon.RxPowerDbm.Should().Be(-19.16);
    }

    [Fact]
    public void ApplyLive_FillsBiasVoltageAndUptime()
    {
        var batch = PeterPonOntProvider.ParseStatusAndInfo($"[{StatusReply()},{InfoReply()}]");
        var stats = PeterPonOntProvider.MapToOntStats(batch.Status!, batch.Info, Context());

        PeterPonOntProvider.ApplyLive(PeterPonOntProvider.ParseLive(LiveReply())!, stats);

        stats.BiasMa.Should().Be(8.53);
        stats.VoltageV.Should().Be(3.35);
        stats.Pon!.VoltageV.Should().Be(3.35);
        stats.Pon.SfpUptimeS.Should().Be(134171);
    }

    // --- Error counters ---

    private static PeterPonOntProvider.LiveSample Samples(long[] times, long[] bip, long[]? fecUncorr = null) =>
        new(1, null, null, null)
        {
            Times = times,
            Rates = new Dictionary<string, IReadOnlyList<long?>>
            {
                [PeterPonOntProvider.BipRate] = bip.Select(v => (long?)v).ToList(),
                [PeterPonOntProvider.FecUncorrectedRate] = (fecUncorr ?? new long[bip.Length]).Select(v => (long?)v).ToList(),
            },
        };

    private static OntStats StatsWithPon() => new() { Pon = new() };

    [Fact]
    public void ParseLive_Fixture_ReadsErrorRatesAlignedWithTimes()
    {
        var sample = PeterPonOntProvider.ParseLive(LiveReply())!;

        sample.Times.Should().Equal(134169015, 134170016, 134171016);
        sample.Rates.Keys.Should().BeEquivalentTo(
            PeterPonOntProvider.BipRate, PeterPonOntProvider.FecUncorrectedRate, PeterPonOntProvider.FecCorrectedRate,
            PeterPonOntProvider.HecCorrectedRate, PeterPonOntProvider.HecUncorrectedRate);
        sample.Rates[PeterPonOntProvider.BipRate].Should().Equal(0L, 0L, 0L);
    }

    [Fact]
    public void ApplyErrorTotals_Fixture_ReportsZeroCounters()
    {
        var stats = StatsWithPon();

        PeterPonOntProvider.ApplyErrorTotals(new(), PeterPonOntProvider.ParseLive(LiveReply())!, stats);

        stats.BipErrors.Should().Be(0);
        stats.FecErrors.Should().Be(0);
        stats.Pon!.FecCorrectedWords.Should().Be(0);
        stats.Pon.HecCorrected.Should().Be(0);
        stats.Pon.HecUncorrected.Should().Be(0);
    }

    [Fact]
    public void ApplyErrorTotals_SumsAcrossPolls()
    {
        var totals = new PeterPonOntProvider.ErrorTotals();
        var stats = StatsWithPon();

        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([1000, 2000], [3, 0], [1, 1]), stats);
        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([3000, 4000], [0, 4], [0, 2]), stats);

        stats.BipErrors.Should().Be(7);
        stats.FecErrors.Should().Be(4);
        stats.Pon!.BipErrors.Should().Be(7);
        stats.Pon.FecErrors.Should().Be(4);
    }

    [Fact]
    public void ApplyErrorTotals_RepeatedSamples_CountOnce()
    {
        // since fell back to 0, so the reply repeats samples already counted.
        var totals = new PeterPonOntProvider.ErrorTotals();
        var stats = StatsWithPon();

        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([1000, 2000], [3, 2]), stats);
        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([1000, 2000, 3000], [3, 2, 1]), stats);

        stats.BipErrors.Should().Be(6);
    }

    [Fact]
    public void ApplyErrorTotals_StickReboot_StartsOver()
    {
        var totals = new PeterPonOntProvider.ErrorTotals();
        var stats = StatsWithPon();

        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([500_000, 501_000], [10, 10]), stats);
        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([20_000, 21_000], [1, 2]), stats);

        stats.BipErrors.Should().Be(3, "sample times went back, so the stick rebooted");
    }

    [Fact]
    public void ApplyErrorTotals_NoNewSamples_KeepsReportingTotals()
    {
        var totals = new PeterPonOntProvider.ErrorTotals();
        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([1000], [5]), StatsWithPon());
        var stats = StatsWithPon();

        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([], []), stats);

        stats.BipErrors.Should().Be(5);
    }

    [Fact]
    public void ApplyErrorTotals_ShortMetricArray_AlignsFromNewest()
    {
        var totals = new PeterPonOntProvider.ErrorTotals();
        var stats = StatsWithPon();
        var sample = new PeterPonOntProvider.LiveSample(1, null, null, null)
        {
            Times = [1000, 2000, 3000],
            Rates = new Dictionary<string, IReadOnlyList<long?>> { [PeterPonOntProvider.BipRate] = [4L, 6L] },
        };

        PeterPonOntProvider.ApplyErrorTotals(totals, sample, stats);
        PeterPonOntProvider.ApplyErrorTotals(totals, Samples([3000, 4000], [6, 1]), stats);

        stats.BipErrors.Should().Be(11, "the two values are the newest two samples; 3000 ms is not counted twice");
    }

    [Fact]
    public void ApplyErrorTotals_FirmwareWithoutRates_LeavesCountersNull()
    {
        var stats = StatsWithPon();
        var sample = new PeterPonOntProvider.LiveSample(1, null, null, null) { Times = [1000] };

        PeterPonOntProvider.ApplyErrorTotals(new(), sample, stats);

        stats.BipErrors.Should().BeNull();
        stats.FecErrors.Should().BeNull();
    }

    [Fact]
    public void MapToOntStats_Ranging_ReportsDown()
    {
        var status = new PeterPonStatus { PloamState = 4, RxPower = "0.00uW -infdBm" };

        var stats = PeterPonOntProvider.MapToOntStats(status, null, Context());

        stats.PonLinkStatus.Should().Be(PonLinkState.Ranging);
        stats.OperationalStatus.Should().Be("Down");
        stats.RxPowerDbm.Should().BeNull();
        stats.VendorSn.Should().BeNull();
        stats.Pon!.FirmwareVersion.Should().BeNull();
    }

    [Fact]
    public void MapToOntStats_UnknownState_LeavesStateFieldsNull()
    {
        var stats = PeterPonOntProvider.MapToOntStats(new PeterPonStatus { PloamState = 0 }, null, Context());

        stats.PonLinkStatus.Should().Be(PonLinkState.Unknown);
        stats.LinkState.Should().BeNull();
        stats.OperationalStatus.Should().BeNull();
        stats.Pon!.PonLinkStatus.Should().BeNull();
        stats.Pon.PloamStateRaw.Should().BeNull();
    }

    // --- Session and live_get flow against a stub ---

    [Fact]
    public async Task Poll_SignsInAndReadsStatusInfoAndLive()
    {
        using var stub = new StubUbus("root", "secret");

        var result = await NewProvider().PollAsync(Context(stub.Port, password: "secret"));

        result.Success.Should().BeTrue(result.FailureReason);
        result.Stats!.RxPowerDbm.Should().Be(-19.16);
        result.Stats.BiasMa.Should().Be(8.53);
        result.Stats.Pon!.SfpUptimeS.Should().Be(134171);
        result.Stats.BipErrors.Should().Be(0);
        result.Stats.FecErrors.Should().Be(0);
        stub.Logins.Should().Be(1);
        stub.Paths.Should().OnlyContain(p => p == "/ubus/");
    }

    [Fact]
    public async Task Poll_ReusesSessionAcrossPolls()
    {
        using var stub = new StubUbus("root", "secret");
        var provider = NewProvider();

        await provider.PollAsync(Context(stub.Port, password: "secret"));
        var second = await provider.PollAsync(Context(stub.Port, password: "secret"));

        second.Success.Should().BeTrue(second.FailureReason);
        stub.Logins.Should().Be(1);
    }

    [Fact]
    public async Task Poll_PassesPreviousSeqAsSince()
    {
        using var stub = new StubUbus("root", "secret");
        var provider = NewProvider();

        await provider.PollAsync(Context(stub.Port, password: "secret"));
        await provider.PollAsync(Context(stub.Port, password: "secret"));

        stub.Since.Should().Equal(0, 6237);
    }

    [Fact]
    public async Task Poll_FailedLiveRead_StartsOverFromZero()
    {
        using var stub = new StubUbus("root", "secret");
        var provider = NewProvider();

        await provider.PollAsync(Context(stub.Port, password: "secret"));
        stub.LiveFails = true;
        var degraded = await provider.PollAsync(Context(stub.Port, password: "secret"));
        stub.LiveFails = false;
        await provider.PollAsync(Context(stub.Port, password: "secret"));

        degraded.Success.Should().BeTrue("live_get is best effort");
        degraded.Stats!.BiasMa.Should().BeNull();
        stub.Since.Should().Equal(0, 6237, 0);
    }

    [Fact]
    public async Task TestConnection_LeavesSavedConfigurationStateAlone()
    {
        using var stub = new StubUbus("root", "secret");
        var provider = NewProvider();

        await provider.PollAsync(Context(stub.Port, password: "secret"));
        await provider.TestConnectionAsync(Context(stub.Port, password: "secret"));
        await provider.PollAsync(Context(stub.Port, password: "secret"));

        stub.Since.Should().Equal(0, 0, 6237);
        stub.Logins.Should().Be(2, "the Test button signs in on its own and the saved session is kept");
    }

    [Fact]
    public async Task Poll_ExpiredSession_SignsInAgain()
    {
        using var stub = new StubUbus("root", "secret");
        var provider = NewProvider();

        await provider.PollAsync(Context(stub.Port, password: "secret"));
        stub.ExpireSessions();
        var second = await provider.PollAsync(Context(stub.Port, password: "secret"));

        second.Success.Should().BeTrue(second.FailureReason);
        stub.Logins.Should().Be(2);
    }

    [Fact]
    public async Task Poll_WrongPassword_FailsWithSignInMessage()
    {
        using var stub = new StubUbus("root", "secret");

        var result = await NewProvider().PollAsync(Context(stub.Port, password: "wrong"));

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("rejected the sign-in");
    }

    [Fact]
    public async Task Poll_ChangedCredentials_SignInAgain()
    {
        using var stub = new StubUbus("admin", "secret");
        var provider = NewProvider();

        (await provider.PollAsync(Context(stub.Port, password: "secret"))).Success.Should().BeFalse();
        var fixedCreds = await provider.PollAsync(Context(stub.Port, username: "admin", password: "secret"));

        fixedCreds.Success.Should().BeTrue(fixedCreds.FailureReason);
    }

    [Fact]
    public async Task Poll_SessionWithoutLuciGponAccess_SaysSignInAsRoot()
    {
        using var stub = new StubUbus("root", "secret") { DenyLuciGpon = true };

        var result = await NewProvider().PollAsync(Context(stub.Port, password: "secret"));

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("denied access to luci.gpon");
        stub.Logins.Should().Be(2, "the first denial is read as an expired session and retried once");
    }

    [Fact]
    public async Task Poll_NoLuciGpon_SaysCheckFirmware()
    {
        using var stub = new StubUbus("root", "secret", hasLuciGpon: false);

        var result = await NewProvider().PollAsync(Context(stub.Port, password: "secret"));

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("Peter-Pon");
    }

    [Fact]
    public async Task TestConnection_ReportsStateAndRx()
    {
        using var stub = new StubUbus("root", "secret");

        var (success, message) = await NewProvider().TestConnectionAsync(Context(stub.Port, password: "secret"));

        success.Should().BeTrue(message);
        message.Should().Be("Connected - Connected (O5), RX -19.16 dBm");
    }

    [Fact]
    public async Task PollSupplemental_ReturnsPonDetail()
    {
        using var stub = new StubUbus("root", "secret");

        var pon = await NewProvider().PollSupplementalAsync(Context(stub.Port, password: "secret"));

        pon.Should().NotBeNull();
        pon!.PonLinkStatus.Should().Be("operation");
        pon.OltVendorId.Should().Be("HWTC");
        pon.VoltageV.Should().Be(3.35);
    }

    [Fact]
    public void ProviderKey_IsStable()
    {
        NewProvider().ProviderKey.Should().Be("peter-pon");
    }

    /// <summary>
    /// A uhttpd /ubus/ stand-in that answers with the captured replies: session.login issues a token
    /// for the right credentials, and luci.gpon calls answer -32002 Access denied without a live
    /// token. Accepts single and batch requests, and records each live_get since.
    /// </summary>
    private sealed class StubUbus : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _username;
        private readonly string _password;
        private readonly bool _hasLuciGpon;
        private readonly HashSet<string> _sessions = [];
        private readonly List<string> _paths = [];
        private readonly List<long> _since = [];
        private int _logins;

        public int Port { get; }
        public int Logins => Volatile.Read(ref _logins);
        public bool LiveFails { get; set; }
        public bool DenyLuciGpon { get; init; }

        public IReadOnlyList<string> Paths
        {
            get { lock (_paths) return _paths.ToList(); }
        }

        public IReadOnlyList<long> Since
        {
            get { lock (_since) return _since.ToList(); }
        }

        public StubUbus(string username, string password, bool hasLuciGpon = true)
        {
            _username = username;
            _password = password;
            _hasLuciGpon = hasLuciGpon;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public void ExpireSessions()
        {
            lock (_sessions) _sessions.Clear();
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
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            var lines = received.ToString()[..headerEnd].Split("\r\n");
            lock (_paths) _paths.Add(lines[0].Split(' ')[1]);
            var lengthLine = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthLine != null && int.TryParse(lengthLine[15..].Trim(), out var n) ? n : 0;
            while (received.Length < headerEnd + 4 + length)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                    break;
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            var request = JsonNode.Parse(received.ToString()[(headerEnd + 4)..])!;
            var reply = request is JsonArray batch
                ? "[" + string.Join(",", batch.Select(c => Answer(c!))) + "]"
                : Answer(request);

            var bodyBytes = Encoding.UTF8.GetBytes(reply);
            var head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                       $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token);
            await stream.WriteAsync(bodyBytes, _cts.Token);
        }

        private string Answer(JsonNode call)
        {
            var id = call["id"]!.GetValue<int>();
            var p = call["params"]!.AsArray();
            var session = p[0]!.GetValue<string>();
            var obj = p[1]!.GetValue<string>();
            var method = p[2]!.GetValue<string>();

            if (obj == "session" && method == "login")
            {
                var args = p[3]!;
                if (args["username"]!.GetValue<string>() != _username || args["password"]!.GetValue<string>() != _password)
                    return $$"""{"jsonrpc":"2.0","id":{{id}},"result":[6]}""";
                Interlocked.Increment(ref _logins);
                var token = Guid.NewGuid().ToString("N");
                lock (_sessions) _sessions.Add(token);
                return Ok(id, $$"""{"ubus_rpc_session":"{{token}}","timeout":300,"expires":300}""");
            }

            bool live;
            lock (_sessions) live = _sessions.Contains(session);
            if (!live || DenyLuciGpon)
                return $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32002,"message":"Access denied"}}""";
            if (!_hasLuciGpon)
                return $$"""{"jsonrpc":"2.0","id":{{id}},"result":[4]}""";

            if (method == "live_get")
            {
                lock (_since) _since.Add(p[3]!["since"]!.GetValue<long>());
                return LiveFails ? $$"""{"jsonrpc":"2.0","id":{{id}},"result":[5]}""" : LiveReply(id);
            }

            return method switch
            {
                "status" => StatusReply(id),
                "info" => InfoReply(id),
                _ => $$"""{"jsonrpc":"2.0","id":{{id}},"result":[3]}""",
            };
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
