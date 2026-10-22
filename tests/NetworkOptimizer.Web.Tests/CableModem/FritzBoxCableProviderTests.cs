using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

public class FritzBoxCableProviderTests
{
    private static FritzBoxCableProvider Provider() => new(NullLogger<FritzBoxCableProvider>.Instance);
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "CableModem", "Fixtures", "fritzbox-6690-docinfo.json"));
    private static CmPollContext Context(IDeviceDialer? dialer = null) => new()
    {
        Id = 0, SiteSlug = "test-site", Host = "192.0.2.10", Name = "Cable",
        Username = "test-user", Password = "äbc", Dialer = dialer ?? DirectDeviceDialer.Instance,
    };

    private static JsonElement Payload(Action<JsonNode>? edit = null)
    {
        var node = JsonNode.Parse(Fixture)!;
        edit?.Invoke(node);
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    [Fact]
    public void Parse_Captured6690_ReadsAllChannelsAndNormalizesRfUnits()
    {
        var stats = FritzBoxCableProvider.Parse(Payload(), Context());
        stats.DownstreamChannels.Should().HaveCount(26);
        stats.UpstreamChannels.Should().HaveCount(4);
        stats.LockedDsChannels.Should().Be(26);
        stats.LockedUsChannels.Should().Be(4);
        stats.DownstreamChannels.Select(c => c.ChannelId).Should().OnlyHaveUniqueItems();
        stats.UpstreamChannels.Select(c => c.ChannelId).Should().OnlyHaveUniqueItems();
        // These feed the dashboard, Influx history and alerts; include all RF channels,
        // but only SC-QAM counters, as independently summed from the captured payload.
        stats.DownstreamPowerAvgDbmv.Should().BeApproximately(4.9923076923, 0.000001);
        stats.DownstreamSnrAvgDb.Should().BeApproximately(37.8, 0.000001);
        stats.UpstreamPowerAvgDbmv.Should().BeApproximately(42.275, 0.000001);
        stats.TotalCorrectables.Should().Be(985);
        stats.TotalUncorrectables.Should().Be(4106);
        stats.ChannelsWithUncorrectables.Should().Be(2);
        var scqam = stats.DownstreamChannels.Single(c => c.ChannelId == 6);
        scqam.Frequency.Should().Be(594_000_000);
        scqam.Modulation.Should().Be("QAM256");
        scqam.Power.Should().Be(5.1);
        scqam.Snr.Should().Be(37.6);
        scqam.Correctables.Should().Be(7);
        var ofdm = stats.DownstreamChannels.Single(c => c.ChannelId == 33);
        ofdm.Modulation.Should().Be("OFDM");
        ofdm.Frequency.Should().Be(229_975_000);
        ofdm.Snr.Should().Be(40);
        var ofdma = stats.UpstreamChannels.Single(c => c.ChannelId == 5);
        ofdma.ChannelType.Should().Be("OFDMA");
        ofdma.Modulation.Should().Be("QAM32");
        ofdma.Frequency.Should().Be(56_425_000);
        ofdma.Power.Should().Be(39);
        ofdma.SymbolRate.Should().Be(0);
        stats.UpstreamChannels.Where(c => c.ChannelId != 5).Should().OnlyContain(c => c.ChannelType == "ATDMA");
        stats.DocsisState.Should().BeNull();
        stats.UptimeSeconds.Should().BeNull();
        stats.Events.Should().BeEmpty();
    }

    [Fact]
    public void Parse_OfdmCountersCannotContaminateScQamTotals()
    {
        var stats = FritzBoxCableProvider.Parse(Payload(n =>
        {
            n["data"]!["channelDs"]!["docsis31"]![1]!["nonCorrErrors"] = 9_000_000_000L;
            n["data"]!["channelDs"]!["docsis31"]![1]!["corrErrors"] = 8_000_000_000L;
            n["data"]!["channelDs"]!["docsis30"]![0]!["corrErrors"] = "5000000000";
        }), Context());
        stats.DownstreamChannels.Where(c => c.Modulation == "OFDM")
            .Should().OnlyContain(c => c.Correctables == 0 && c.Uncorrectables == 0);
        stats.DownstreamChannels[0].Correctables.Should().Be(5_000_000_000L);
        stats.TotalCorrectables.Should().BeLessThan(8_000_000_000L);
        stats.TotalUncorrectables.Should().BeLessThan(9_000_000_000L);
    }

    [Fact]
    public void Parse_Docsis30DoesNotRequireDocsis31Arrays()
    {
        var stats = FritzBoxCableProvider.Parse(Payload(n =>
        {
            n["data"]!["channelDs"]!.AsObject().Remove("docsis31");
            n["data"]!["channelUs"]!.AsObject().Remove("docsis31");
        }), Context());
        stats.DownstreamChannels.Should().HaveCount(24);
        stats.UpstreamChannels.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_EmptyChannelArraysAreNotAnUnrelatedResponse()
    {
        var stats = FritzBoxCableProvider.Parse(Payload(n =>
        {
            foreach (var direction in new[] { "channelDs", "channelUs" })
                foreach (var version in new[] { "docsis30", "docsis31" })
                    n["data"]![direction]![version]!.AsArray().Clear();
        }), Context());
        stats.LockedDsChannels.Should().Be(0);
        stats.DownstreamPowerAvgDbmv.Should().BeNull();
        stats.UpstreamChannels.Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnusedSlotsAndMissingOptionalRfDoNotCreateMeasuredZeros()
    {
        var stats = FritzBoxCableProvider.Parse(Payload(n =>
        {
            n["data"]!["channelDs"]!["docsis30"]![0]!["powerLevel"] = null;
            n["data"]!["channelDs"]!["docsis30"]![0]!["mse"] = null;
            n["data"]!["channelDs"]!["docsis30"]!.AsArray().Add(JsonNode.Parse(
                """{"channelID":0,"frequency":"0.000","powerLevel":"0","mse":"0"}"""));
        }), Context());
        stats.DownstreamChannels.Should().HaveCount(26);
        stats.DownstreamChannels[0].Power.Should().BeNull();
        stats.DownstreamChannels[0].Snr.Should().BeNull();
    }

    [Theory]
    [InlineData("channelDs")]
    [InlineData("channelUs")]
    public void Parse_MissingDirectionalContainerIsNotAHealthyEmptyModem(string direction)
    {
        var parse = () => FritzBoxCableProvider.Parse(Payload(n => n["data"]!.AsObject().Remove(direction)), Context());
        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public void Parse_InvalidScQamCounterFailsInsteadOfReportingZero(string? value)
    {
        var parse = () => FritzBoxCableProvider.Parse(Payload(n =>
            n["data"]!["channelDs"]!["docsis30"]![0]!["corrErrors"] = value), Context());
        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("594.000", 594_000_000L)]
    [InlineData("134.975 - 324.975", 229_975_000L)]
    [InlineData("48.025 - 64.825", 56_425_000L)]
    public void Frequency_UsesHzAndRepresentativeMidpoint(string value, long expected) =>
        FritzBoxCableProvider.Frequency(value).Should().Be(expected);

    [Theory]
    [InlineData("594,000")]
    [InlineData("0")]
    [InlineData("200 - 100")]
    [InlineData("not a frequency")]
    public void Frequency_RejectsInvalidBands(string value)
    {
        var parse = () => FritzBoxCableProvider.Frequency(value);
        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void CalculateResponse_MatchesAvmPbkdf2Vector() =>
        FritzBoxCableProvider.CalculateResponse("2$10000$5A1711$2000$5A1722", "1example!").Should()
            .Be("5A1722$1798a1672bca7c6463d6b245f82b53703b0f50813401b03e4045a5861e689adb");

    [Fact]
    public void CalculateResponse_MatchesAvmLegacyVectorAndUnicodeReplacement()
    {
        FritzBoxCableProvider.CalculateResponse("1234567z", "äbc").Should()
            .Be("1234567z-9e224a41eeefa284df7bb0f26c2913e2");
        FritzBoxCableProvider.CalculateResponse("1234567z", "€😀").Should()
            .Be(FritzBoxCableProvider.CalculateResponse("1234567z", ".."));
    }

    [Theory]
    [InlineData("3$10000$5A1711$2000$5A1722")]
    [InlineData("2$0$aa$1$bb")]
    [InlineData("2$1000001$aa$1$bb")]
    [InlineData("2$1$aa$1")]
    [InlineData("")]
    public void CalculateResponse_RejectsUnknownOrMalformedChallenges(string challenge)
    {
        var calculate = () => FritzBoxCableProvider.CalculateResponse(challenge, "test");
        calculate.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Poll_UsesDialerAndPostsCredentialsThenReadsAndLogsOut()
    {
        await using var device = new TestDevice();
        var provider = Provider();
        var result = await provider.PollAsync(Context(device));
        result.Success.Should().BeTrue(result.FailureReason);
        result.Stats!.DeviceModel.Should().Be("FRITZ!Box 6690 Cable");
        result.Stats.FirmwareVersion.Should().Be("8.40");
        device.Opens.Should().OnlyContain(o => o.Host == "192.0.2.10" && o.Port == 80);
        device.Requests.Should().OnlyContain(r => r.Host == "192.0.2.10" && !r.Path.Contains("sid="));
        var login = device.Requests.Single(r => r.Form.ContainsKey("response"));
        login.Method.Should().Be("POST");
        login.Form["username"].Should().Be("test-user");
        login.Form["response"].Should().Be("1234567z-9e224a41eeefa284df7bb0f26c2913e2");
        var data = device.Requests.Single(r => r.Path == "/data.lua");
        data.Form["page"].Should().Be("docInfo");
        data.Form["xhrId"].Should().Be("all");
        device.Logouts.Should().Equal(data.Form["sid"]);
        provider.Credentials.Should().Be(CmCredentialRequirement.Required);
        provider.BlankUsernameMeansAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task Poll_Pbkdf2LoginPreservesFormEncodingAndUsesTheIssuedSession()
    {
        await using var device = new TestDevice();
        const string username = "cable+über&stats=1";
        const string response = "5A1722$1798a1672bca7c6463d6b245f82b53703b0f50813401b03e4045a5861e689adb";
        device.Respond = (r, _) =>
        {
            if (r.Method == "GET" && r.Path == "/login_sid.lua?version=2")
                return Task.FromResult(new Reply(Session(challenge: "2$10000$5A1711$2000$5A1722")));
            if (r.Path == "/data.lua" && (!r.Form.TryGetValue("sid", out var sid)
                                        || !device.Sessions.TryGetValue(sid, out var account) || account != username))
                return Task.FromResult(new Reply("", 403));
            return Task.FromResult(device.DefaultReply(r));
        };

        var result = await Provider().PollAsync(Context(device) with { Username = username, Password = "1example!" });

        result.Success.Should().BeTrue(result.FailureReason);
        result.Stats!.LockedDsChannels.Should().Be(26);
        result.Stats.LockedUsChannels.Should().Be(4);
        var login = device.Requests.Single(r => r.Form.ContainsKey("response"));
        login.Path.Should().Be("/login_sid.lua?version=2");
        login.Form["username"].Should().Be(username);
        login.Form["response"].Should().Be(response);
        device.Logouts.Should().Equal(device.Requests.Single(r => r.Path == "/data.lua").Form["sid"]);
    }

    [Fact]
    public async Task TestConnection_ReadsActualChannelsAndFallsBackWhenMetadataFails()
    {
        await using var device = new TestDevice();
        device.Respond = (r, _) => Task.FromResult(r.Path == "/juis_boxinfo.xml"
            ? new Reply("unavailable", 404) : device.DefaultReply(r));
        var (success, message) = await Provider().TestConnectionAsync(Context(device));
        success.Should().BeTrue();
        message.Should().Contain("FRITZ!Box Cable").And.Contain("26 downstream, 4 upstream");
        device.Logouts.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("https://example.invalid/data.lua")]
    [InlineData("//example.invalid/data.lua")]
    [InlineData("/\\example.invalid/data.lua")]
    public async Task Poll_RejectsAuthorityChangingOverrideBeforeConnecting(string path)
    {
        await using var device = new TestDevice();
        var result = await Provider().PollAsync(Context(device) with { StatusPagePath = path });
        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("relative path");
        device.Opens.Should().BeEmpty();
    }

    [Fact]
    public async Task Poll_RespectsRelativeOverrideAndCustomPort()
    {
        await using var device = new TestDevice();
        device.Respond = (r, _) => Task.FromResult(r.Path == "/custom.lua"
            ? new Reply(Fixture) : device.DefaultReply(r));
        var result = await Provider().PollAsync(Context(device) with { Port = 8080, StatusPagePath = "/custom.lua" });
        result.Success.Should().BeTrue(result.FailureReason);
        device.Opens.Should().OnlyContain(o => o.Host == "192.0.2.10" && o.Port == 8080);
    }

    [Fact]
    public async Task Poll_BlockTimeDoesNotAttemptLogin()
    {
        await using var device = new TestDevice();
        device.Respond = (_, _) => Task.FromResult(new Reply(Session(blockTime: 30)));
        var result = await Provider().PollAsync(Context(device));
        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("30 seconds");
        device.Requests.Should().ContainSingle().Which.Method.Should().Be("GET");
    }

    [Fact]
    public async Task Poll_RejectedCredentialsAreNotRetried()
    {
        await using var device = new TestDevice();
        device.Respond = (r, _) => Task.FromResult(r.Form.ContainsKey("response")
            ? new Reply(Session()) : device.DefaultReply(r));
        var result = await Provider().PollAsync(Context(device));
        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("rejected the sign-in");
        device.Requests.Should().HaveCount(2);
        device.Logouts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("<html>Sign in</html>")]
    [InlineData("{\"pid\":\"login\"}")]
    [InlineData("{\"pid\":\"docInfo\",\"data\":{\"readyState\":\"ready\"}}")]
    public async Task Poll_InvalidDataFailsAndStillLogsOut(string body)
    {
        await using var device = new TestDevice();
        device.Respond = (r, _) => Task.FromResult(r.Path == "/data.lua" ? new Reply(body) : device.DefaultReply(r));
        var result = await Provider().PollAsync(Context(device));
        result.Success.Should().BeFalse();
        result.FailureReason.Should().NotBeNullOrWhiteSpace().And.NotContain(body);
        device.Logouts.Should().HaveCount(1);
    }

    [Fact]
    public async Task Poll_DoesNotFollowRedirectOrMaskOutcomeWithLogoutFailure()
    {
        await using var device = new TestDevice();
        device.Respond = (r, _) => Task.FromResult(r.Path == "/data.lua"
            ? new Reply("", 302, "http://example.invalid/elsewhere")
            : r.Form.ContainsKey("logout") ? new Reply("failed", 500) : device.DefaultReply(r));
        var result = await Provider().PollAsync(Context(device));
        result.Success.Should().BeFalse();
        device.Opens.Should().OnlyContain(o => o.Host == "192.0.2.10");
        device.Logouts.Should().HaveCount(1);
    }

    [Fact]
    public async Task Poll_LogoutFailureDoesNotDiscardValidStats()
    {
        await using var device = new TestDevice();
        device.Respond = (r, _) => Task.FromResult(r.Form.ContainsKey("logout")
            ? new Reply("failed", 500) : device.DefaultReply(r));
        (await Provider().PollAsync(Context(device))).Success.Should().BeTrue();
    }

    [Fact]
    public async Task OverlappingPolls_CancellationLogsOutOnlyItsOwnSession()
    {
        await using var device = new TestDevice();
        using var cancelFirst = new CancellationTokenSource();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        device.Respond = async (r, ct) =>
        {
            if (r.Path == "/data.lua")
            {
                if (Interlocked.Increment(ref count) == 2) arrived.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return device.DefaultReply(r);
        };
        var provider = Provider();
        var first = provider.PollAsync(Context(device) with { Username = "alpha" }, cancelFirst.Token);
        var second = provider.PollAsync(Context(device) with { Username = "beta", SiteSlug = "other-site" });
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelFirst.Cancel();
        var cancelled = async () => await first;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        device.Logouts.Should().ContainSingle();
        device.Sessions[device.Logouts.Single()].Should().Be("alpha");
        second.IsCompleted.Should().BeFalse();
        release.SetResult();
        (await second).Success.Should().BeTrue();
        device.Logouts.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task TestConnection_PropagatesCallerCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var device = new TestDevice();
        var test = () => Provider().TestConnectionAsync(Context(device), cancelled.Token);
        await test.Should().ThrowAsync<OperationCanceledException>();
    }

    private static string Session(string sid = "0000000000000000", int blockTime = 0, string challenge = "1234567z") =>
        $"<SessionInfo><SID>{sid}</SID><Challenge>{challenge}</Challenge><BlockTime>{blockTime}</BlockTime></SessionInfo>";

    private sealed record Request(string Method, string Path, string Host, Dictionary<string, string> Form);
    private sealed record Reply(string Body, int Status = 200, string? Location = null);

    // Body-aware loopback device: tests use the real HTTP handler and supplied device dialer.
    private sealed class TestDevice : IDeviceDialer, IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _connections = new();
        private int _session;
        public ConcurrentQueue<Request> Requests { get; } = new();
        public ConcurrentQueue<(string Host, int Port)> Opens { get; } = new();
        public ConcurrentDictionary<string, string> Sessions { get; } = new();
        public IEnumerable<string> Logouts => Requests.Where(r => r.Form.ContainsKey("logout")).Select(r => r.Form["sid"]);
        public Func<Request, CancellationToken, Task<Reply>>? Respond { get; set; }

        public TestDevice()
        {
            _listener.Start();
            _accept = AcceptAsync();
        }

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

        public Reply DefaultReply(Request request)
        {
            if (request.Form.TryGetValue("response", out _))
            {
                var sid = Interlocked.Increment(ref _session).ToString("x16");
                Sessions[sid] = request.Form["username"];
                return new Reply(Session(sid));
            }
            return request.Path switch
            {
                "/data.lua" => new Reply(Fixture),
                "/juis_boxinfo.xml" => new Reply("""
                    <j:BoxInfo xmlns:j="urn:test"><j:Name>FRITZ!Box 6690 Cable</j:Name>
                    <j:Version><j:Major>267</j:Major><j:Minor>8</j:Minor><j:Patch>40</j:Patch></j:Version></j:BoxInfo>
                    """),
                _ => new Reply(Session()),
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
                    var request = new Request(line[0], line[1], headers["Host"], fields);
                    Requests.Enqueue(request);
                    var reply = Respond != null ? await Respond(request, _stop.Token) : DefaultReply(request);
                    var bytes = Encoding.UTF8.GetBytes(reply.Body);
                    var head = $"HTTP/1.1 {reply.Status} Test\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n" +
                               (reply.Location == null ? "" : $"Location: {reply.Location}\r\n") + "\r\n";
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
