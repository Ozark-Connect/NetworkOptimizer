using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using NetworkOptimizer.Web.Tests.Services.RoutedDevices;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

/// <summary>
/// The SB6141's transposed signal page in <see cref="ArrisSurfboardHttpProvider"/>, against pages
/// captured from a real modem, plus the detection chain it joins after the SB8200 and SB6183 steps.
/// </summary>
public class ArrisSurfboardSb6141Tests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "CableModem", "Fixtures");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDir, name));

    private static string Signal => Fixture("arris-sb6141-signal.html");
    private static string Index => Fixture("arris-sb6141-index.html");
    private static string Help => Fixture("arris-sb6141-help.html");

    private static CmPollContext Context(int port = 0, string? statusPagePath = null, int id = 1) => new()
    {
        Id = id,
        SiteSlug = "default",
        Name = "CM1",
        Host = "127.0.0.1",
        Port = port,
        StatusPagePath = statusPagePath,
    };

    private static ArrisSurfboardHttpProvider CreateProvider() =>
        new(NullLogger<ArrisSurfboardHttpProvider>.Instance);

    // ----- Parser -----

    [Fact]
    public void ParseSb6141_ReadsDownstreamColumnsJoinedToCodewords()
    {
        var stats = ArrisSurfboardHttpProvider.ParseSb6141(Signal, Index, Help, Context());

        stats.DownstreamChannels.Select(c => c.ChannelId).Should().Equal(5, 6, 7, 10, 11, 12, 13, 14);
        stats.DownstreamChannels.Should().OnlyContain(c => c.LockStatus == "Locked");

        var ch5 = stats.DownstreamChannels[0];
        ch5.Frequency.Should().Be(489_000_000);
        ch5.Snr.Should().Be(37);
        ch5.Power.Should().Be(-4);
        ch5.Modulation.Should().Be("QAM256");
        ch5.Correctables.Should().Be(3737);
        ch5.Uncorrectables.Should().Be(5964);

        var ch14 = stats.DownstreamChannels[7];
        ch14.Frequency.Should().Be(543_000_000);
        ch14.Correctables.Should().Be(3035);
        ch14.Uncorrectables.Should().Be(10253);
    }

    [Fact]
    public void ParseSb6141_AggregatesMatchTheTrace()
    {
        var stats = ArrisSurfboardHttpProvider.ParseSb6141(Signal, Index, Help, Context());

        stats.LockedDsChannels.Should().Be(8);
        stats.TotalCorrectables.Should().Be(28251);
        stats.TotalUncorrectables.Should().Be(67526);
        stats.ChannelsWithUncorrectables.Should().Be(8);
        stats.DownstreamPowerAvgDbmv.Should().Be(-4);
        stats.DownstreamSnrAvgDb.Should().Be(37);
        stats.LockedUsChannels.Should().Be(4);
        stats.UpstreamPowerAvgDbmv.Should().Be(44.75);
    }

    [Fact]
    public void ParseSb6141_ReadsUpstreamColumnsInPageOrder()
    {
        var stats = ArrisSurfboardHttpProvider.ParseSb6141(Signal, Index, Help, Context());

        stats.UpstreamChannels.Select(c => c.ChannelId).Should().Equal(5, 8, 7, 6);

        var ch5 = stats.UpstreamChannels[0];
        ch5.Frequency.Should().Be(17_800_000);
        ch5.Power.Should().Be(43);
        ch5.SymbolRate.Should().Be(5_120_000);
        ch5.LockStatus.Should().Be("Locked");
        ch5.Modulation.Should().Be("64QAM");
        ch5.ChannelType.Should().BeEmpty();

        stats.UpstreamChannels[1].Frequency.Should().Be(37_000_000);
        stats.UpstreamChannels[1].Power.Should().Be(47);
    }

    [Fact]
    public void ParseSb6141_ReadsModelFirmwareStatusAndUptime()
    {
        var stats = ArrisSurfboardHttpProvider.ParseSb6141(Signal, Index, Help, Context());

        stats.DeviceModel.Should().Be("ARRIS SB6141");
        stats.FirmwareVersion.Should().Be("SB_KOMODO-1.0.7.0-SCM00-NOSH");
        stats.UptimeSeconds.Should().Be(3_368_080);
        stats.DocsisState.Should().NotBeNull();
        stats.DocsisState!.State.Should().Be("Operational");
        stats.DocsisState.Mode.Should().BeNull();
    }

    [Fact]
    public void ParseSb6141_WithoutIndexOrHelp_StillReturnsChannels()
    {
        var stats = ArrisSurfboardHttpProvider.ParseSb6141(Signal, null, null, Context());

        stats.DownstreamChannels.Should().HaveCount(8);
        stats.UpstreamChannels.Should().HaveCount(4);
        stats.DeviceModel.Should().Be("ARRIS SB6141");
        stats.FirmwareVersion.Should().BeNull();
        stats.UptimeSeconds.Should().BeNull();
        stats.DocsisState.Should().BeNull();
    }

    [Fact]
    public void ParseSb6141_SkipsColumnWithZeroFrequency()
    {
        // An unlocked column's rendering is unknown; a zero frequency must not become a channel.
        var html = Signal
            .Replace("<TD>543000000 Hz&nbsp;</TD>", "<TD>0 Hz&nbsp;</TD>")
            .Replace("<TD>24200000 Hz&nbsp;</TD>", "<TD>0 Hz&nbsp;</TD>");

        var stats = ArrisSurfboardHttpProvider.ParseSb6141(html, null, null, Context());

        stats.DownstreamChannels.Select(c => c.ChannelId).Should().Equal(5, 6, 7, 10, 11, 12, 13);
        stats.UpstreamChannels.Select(c => c.ChannelId).Should().Equal(5, 8, 7);
    }

    [Fact]
    public void ParseSb6141_RangingStatusOtherThanSuccessPassesThrough()
    {
        var html = ReplaceFirstAfter(Signal, "Ranging Status", "Success&nbsp;", "Aborted&nbsp;");

        var stats = ArrisSurfboardHttpProvider.ParseSb6141(html, null, null, Context());

        stats.UpstreamChannels[0].LockStatus.Should().Be("Aborted");
        stats.LockedUsChannels.Should().Be(3);
    }

    [Theory]
    [InlineData("[3] QPSK [1] 16QAM [2] 64QAM", "64QAM")]
    [InlineData("[3] QPSK", "QPSK")]
    [InlineData("[1] 16QAM [3] QPSK", "16QAM")]
    [InlineData("", null)]
    public void HighestOrderModulation_PicksTheHighestBurstProfile(string text, string? expected)
    {
        ArrisSurfboardHttpProvider.HighestOrderModulation(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("38 days 23h:34m:40s", 3_368_080L)]
    [InlineData("0 days 00h:01m:05s", 65L)]
    [InlineData("1 day 01h:00m:00s", 90_000L)]
    [InlineData("unknown", null)]
    public void ParseSb6141Uptime_ReadsDaysHoursMinutesSeconds(string text, long? expected)
    {
        ArrisSurfboardHttpProvider.ParseSb6141Uptime(text).Should().Be(expected);
    }

    // ----- Format detection -----

    [Fact]
    public void IsSb6141SignalPage_AcceptsTheSignalPageOnly()
    {
        ArrisSurfboardHttpProvider.IsSb6141SignalPage(Signal).Should().BeTrue();
        ArrisSurfboardHttpProvider.IsSb6141SignalPage(Index).Should().BeFalse();
        ArrisSurfboardHttpProvider.IsSb6141SignalPage(Help).Should().BeFalse();
        ArrisSurfboardHttpProvider.IsSb6141SignalPage(Sb6183Html).Should().BeFalse();
    }

    // ----- Detection chain, end to end on loopback -----

    [Fact]
    public async Task PollAsync_Sb6141_FallsThroughToTheSignalPage()
    {
        var paths = new ConcurrentQueue<string>();
        await using var server = new FakeDeviceHttpServer(Sb6141Modem(paths));

        var result = await CreateProvider().PollAsync(Context(server.Port));

        result.Success.Should().BeTrue();
        result.Stats!.DeviceModel.Should().Be("ARRIS SB6141");
        result.Stats.DownstreamChannels.Should().HaveCount(8);
        result.Stats.UpstreamChannels.Should().HaveCount(4);
        result.Stats.FirmwareVersion.Should().Be("SB_KOMODO-1.0.7.0-SCM00-NOSH");
        result.Stats.UptimeSeconds.Should().Be(3_368_080);
        paths.Should().Contain(new[] { "/RgConnect.asp", "/cmSignalData.htm", "/indexData.htm", "/cmHelpData.htm" });
    }

    [Fact]
    public async Task PollAsync_Sb6141_CachedFormatSkipsTheOtherProbes()
    {
        var paths = new ConcurrentQueue<string>();
        await using var server = new FakeDeviceHttpServer(Sb6141Modem(paths));
        var provider = CreateProvider();

        (await provider.PollAsync(Context(server.Port))).Success.Should().BeTrue();
        paths.Clear();

        var second = await provider.PollAsync(Context(server.Port));

        second.Success.Should().BeTrue();
        second.Stats!.DownstreamChannels.Should().HaveCount(8);
        paths.Should().Equal("/cmSignalData.htm", "/indexData.htm", "/cmHelpData.htm");
    }

    [Fact]
    public async Task PollAsync_Sb6141_UnknownPathAnsweredWithAPage_StillReachesTheSignalPage()
    {
        // If NET-DK answers /RgConnect.asp with 200 and some page, the SB6183 parse finds no channels.
        // That must not stop the chain before the SB6141 step.
        await using var server = new FakeDeviceHttpServer(path => path switch
        {
            "/RgConnect.asp" => NetDk(Index),
            _ => Sb6141Modem(new ConcurrentQueue<string>())(path),
        });

        var result = await CreateProvider().PollAsync(Context(server.Port));

        result.Success.Should().BeTrue();
        result.Stats!.DeviceModel.Should().Be("ARRIS SB6141");
        result.Stats.DownstreamChannels.Should().HaveCount(8);
    }

    [Fact]
    public async Task PollAsync_StatusPagePathSetToSignalPage_ParsesAsSb6141NotSb6183()
    {
        var paths = new ConcurrentQueue<string>();
        await using var server = new FakeDeviceHttpServer(Sb6141Modem(paths));

        var result = await CreateProvider().PollAsync(Context(server.Port, statusPagePath: "/cmSignalData.htm"));

        result.Success.Should().BeTrue();
        result.Stats!.DeviceModel.Should().Be("ARRIS SB6141");
        result.Stats.DownstreamChannels.Should().HaveCount(8);
        result.Stats.UpstreamChannels.Should().HaveCount(4);
        result.Stats.UpstreamChannels.Should().OnlyContain(c => c.ChannelId > 0);
    }

    [Fact]
    public async Task PollAsync_Sb6141_MalformedHeaderFallsBackToLenientReader()
    {
        // A header line that lost its colon, as NET-DK does on the CM700, makes HttpClient throw.
        await using var server = new FakeDeviceHttpServer(path => path switch
        {
            "/cmSignalData.htm" => NetDk(Signal, extraHeaderLine: "ceived response error"),
            _ => Sb6141Modem(new ConcurrentQueue<string>())(path),
        });

        var result = await CreateProvider().PollAsync(Context(server.Port));

        result.Success.Should().BeTrue();
        result.Stats!.DownstreamChannels.Should().HaveCount(8);
    }

    [Fact]
    public async Task TestConnectionAsync_Sb6141_NamesTheModel()
    {
        await using var server = new FakeDeviceHttpServer(Sb6141Modem(new ConcurrentQueue<string>()));

        var (success, message) = await CreateProvider().TestConnectionAsync(Context(server.Port));

        success.Should().BeTrue();
        message.Should().Be("Connected to ARRIS SB6141 (HTTP)");
    }

    [Fact]
    public async Task PollAsync_Sb6183_IsUnchangedAndNeverProbesTheSignalPage()
    {
        var paths = new ConcurrentQueue<string>();
        await using var server = new FakeDeviceHttpServer(path =>
        {
            paths.Enqueue(path);
            return path == "/RgConnect.asp" ? FakeDeviceHttpServer.Ok(Sb6183Html, "text/html") : NotFound;
        });

        var provider = CreateProvider();
        var result = await provider.PollAsync(Context(server.Port));
        var (success, message) = await provider.TestConnectionAsync(Context(server.Port));

        result.Success.Should().BeTrue();
        result.Stats!.DeviceModel.Should().Be("ARRIS SB6183");
        result.Stats.DownstreamChannels.Should().ContainSingle().Which.Frequency.Should().Be(591_000_000);
        result.Stats.UpstreamChannels.Should().ContainSingle().Which.ChannelType.Should().Be("ATDMA");
        success.Should().BeTrue();
        message.Should().Be("Connected to ARRIS SB6183 (HTTP)");
        paths.Should().NotContain("/cmSignalData.htm");
    }

    [Fact]
    public async Task PollAsync_Sb6183PageWithNoChannels_StillReportedOkWhenNoSb6141Answers()
    {
        // Before the SB6141 step existed, any 200 on the SB6183 path was Ok; that outcome holds.
        await using var server = new FakeDeviceHttpServer(path =>
            path == "/RgConnect.asp" ? FakeDeviceHttpServer.Ok("<html><body>no tables</body></html>", "text/html") : NotFound);

        var result = await CreateProvider().PollAsync(Context(server.Port));

        result.Success.Should().BeTrue();
        result.Stats!.DeviceModel.Should().Be("ARRIS SB6183");
        result.Stats.DownstreamChannels.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_NothingAnswers_Fails()
    {
        await using var server = new FakeDeviceHttpServer(_ => NotFound);

        var result = await CreateProvider().PollAsync(Context(server.Port));

        result.Success.Should().BeFalse();
    }

    // ----- Helpers -----

    private const string NotFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    /// <summary>
    /// The SB6141's response shape: no Content-Length, body ended by closing the connection.
    /// </summary>
    private static string NetDk(string body, string? extraHeaderLine = null) =>
        "HTTP/1.1 200 Ok\r\n" +
        "Pragma: no-cache\r\n" +
        "Content-Type: text/html\r\n" +
        (extraHeaderLine != null ? extraHeaderLine + "\r\n" : "") +
        "X-Frame-Options: SAMEORIGIN\r\n" +
        "Expires: -1\r\n" +
        "Connection: close\r\n" +
        "Server: NET-DK/1.0\r\n" +
        "\r\n" +
        body;

    private static Func<string, string> Sb6141Modem(ConcurrentQueue<string> paths) => path =>
    {
        paths.Enqueue(path);
        return path switch
        {
            "/cmSignalData.htm" => NetDk(Signal),
            "/indexData.htm" => NetDk(Index),
            "/cmHelpData.htm" => NetDk(Help),
            _ => NotFound,
        };
    };

    private static string ReplaceFirstAfter(string text, string anchor, string oldValue, string newValue)
    {
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        var idx = text.IndexOf(oldValue, start, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, idx), newValue, text.AsSpan(idx + oldValue.Length));
    }

    // RgConnect.asp shape the SB6183 parser reads: downstream in the third table, upstream in the fourth.
    private const string Sb6183Html = """
        <html><body>
        <table><tr><th>Startup Procedure</th></tr><tr><td>Acquire Downstream Channel</td><td>Locked</td></tr></table>
        <table><tr><th>Connection Status</th></tr><tr><td>Online</td></tr></table>
        <table>
          <tr><th colspan="8">Downstream Bonded Channels</th></tr>
          <tr><td>1</td><td>Locked</td><td>QAM256</td><td>591000000 Hz</td><td>2.5 dBmV</td><td>40.1 dB</td><td>10</td><td>2</td></tr>
        </table>
        <table>
          <tr><th colspan="7">Upstream Bonded Channels</th></tr>
          <tr><td>1</td><td>Locked</td><td>ATDMA</td><td>36000000 Hz</td><td>44.0 dBmV</td></tr>
        </table>
        </body></html>
        """;
}
