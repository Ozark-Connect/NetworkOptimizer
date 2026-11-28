using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// LAN Speed Test history paging and search. SQLite in-memory rather than the EF InMemory provider:
/// the hop search runs json_each, which only a real SQLite connection has.
/// </summary>
public class SpeedTestHistoryQueryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection;
    private readonly NetworkOptimizerDbContext _context;
    private readonly SpeedTestRepository _repository;

    public SpeedTestHistoryQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new NetworkOptimizerDbContext(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _repository = new SpeedTestRepository(_context, NullLogger<SpeedTestRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static Iperf3Result Result(string host, int minutesAfterT0, string? name = null,
        SpeedTestDirection direction = SpeedTestDirection.ServerToDevice, PathAnalysisResult? path = null) => new()
    {
        DeviceHost = host,
        DeviceName = name,
        Direction = direction,
        TestTime = T0.AddMinutes(minutesAfterT0),
        Success = true,
        RawDownloadJson = "{\"raw\":\"down\"}",
        RawUploadJson = "{\"raw\":\"up\"}",
        PathAnalysis = path,
    };

    private static PathAnalysisResult PathThrough(params NetworkHop[] hops) =>
        new() { Path = new NetworkPath { IsValid = true, Hops = hops.ToList() } };

    private async Task SeedAsync(params Iperf3Result[] results)
    {
        _context.Iperf3Results.AddRange(results);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Page_IsNewestFirst_AndCountsTheWholeSet()
    {
        await SeedAsync(Enumerable.Range(0, 25).Select(i => Result($"192.0.2.{i}", i)).ToArray());

        var page = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter(), page: 2, pageSize: 10);

        page.Page.Should().Be(2);
        page.Total.Should().Be(25);
        page.TotalUnfiltered.Should().Be(25);
        page.Items.Select(r => r.DeviceHost).Should().Equal(
            Enumerable.Range(5, 10).Reverse().Select(i => $"192.0.2.{i}"));
    }

    [Fact]
    public async Task Page_PastTheEnd_ClampsToTheLastPage()
    {
        await SeedAsync(Enumerable.Range(0, 12).Select(i => Result($"192.0.2.{i}", i)).ToArray());

        var page = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter(), page: 9, pageSize: 10);

        page.Page.Should().Be(2);
        page.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Page_ExcludesWanResults()
    {
        await SeedAsync(
            Result("192.0.2.1", 0),
            Result("192.0.2.2", 1, direction: SpeedTestDirection.BrowserToServer),
            Result("wan", 2, direction: SpeedTestDirection.CloudflareWan),
            Result("wan", 3, direction: SpeedTestDirection.UwnWanGateway),
            Result("wan", 4, direction: SpeedTestDirection.OpenSpeedTestWan));

        var page = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter(), 1, 10);

        page.TotalUnfiltered.Should().Be(2);
        page.Items.Select(r => r.DeviceHost).Should().Equal("192.0.2.2", "192.0.2.1");
    }

    [Fact]
    public async Task Items_LeaveOutTheRawJson_AndKeepEveryOtherColumn()
    {
        var stored = Result("192.0.2.1", 0, "Desk-PC",
            path: PathThrough(new NetworkHop { DeviceName = "Core-Switch", Type = HopType.Switch }));
        stored.ClientMac = "aa:bb:cc:dd:ee:ff";
        stored.Notes = "after the cable swap";
        stored.Latitude = 1.5;
        stored.WifiSignalDbm = -55;
        stored.DownloadBitsPerSecond = 940_000_000;
        await SeedAsync(stored);

        var item = (await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter(), 1, 10)).Items.Single();

        item.RawDownloadJson.Should().BeNull();
        item.RawUploadJson.Should().BeNull();
        item.Should().BeEquivalentTo(stored, o => o
            .Excluding(r => r.RawDownloadJson)
            .Excluding(r => r.RawUploadJson)
            .Excluding(r => r.PathAnalysis));
        item.PathAnalysis!.Path.Hops.Single().DeviceName.Should().Be("Core-Switch");
    }

    [Theory]
    [InlineData("192.0.2.1")]      // device host
    [InlineData("desk")]           // device name, case-insensitive
    [InlineData("AA:BB")]          // client MAC
    [InlineData("core-switch")]    // hop name, only in the path
    [InlineData("00:11:22")]       // hop MAC
    [InlineData("198.51.100.7")]   // hop IP
    public async Task Search_MatchesTheSameFieldsAsTheInMemoryHelper(string search)
    {
        var match = Result("192.0.2.1", 1, "Desk-PC", path: PathThrough(
            new NetworkHop { DeviceName = "Core-Switch", DeviceMac = "00:11:22:33:44:55", DeviceIp = "198.51.100.7" }));
        match.ClientMac = "aa:bb:cc:dd:ee:ff";
        var other = Result("203.0.113.9", 2, "Lab-Box", path: PathThrough(
            new NetworkHop { DeviceName = "Edge-Switch", DeviceMac = "66:77:88:99:00:01", DeviceIp = "203.0.113.1" }));
        await SeedAsync(match, other);

        var page = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter(search), 1, 10);

        page.Items.Select(r => r.DeviceHost).Should().Equal("192.0.2.1");
        page.Total.Should().Be(1);
        page.TotalUnfiltered.Should().Be(2);
        var normalized = search.Trim().ToLowerInvariant();
        SpeedTestFilterHelper.MatchesFilter(match, normalized).Should().BeTrue();
        SpeedTestFilterHelper.MatchesFilter(other, normalized).Should().BeFalse();
    }

    [Fact]
    public async Task Search_DoesNotMatchOtherPathFields()
    {
        // "Switch" is a hop Type, not a name, MAC, or IP: the text of the JSON must not match.
        await SeedAsync(Result("192.0.2.1", 0, "Desk-PC",
            path: PathThrough(new NetworkHop { DeviceName = "Core", Type = HopType.Switch })));

        var page = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter("switch"), 1, 10);

        page.Total.Should().Be(0);
    }

    [Fact]
    public async Task Search_SkipsARowWithMalformedPathJson()
    {
        var broken = Result("192.0.2.1", 0, "Desk-PC");
        broken.PathAnalysisJson = "{not json";
        await SeedAsync(broken, Result("192.0.2.2", 1, "Lab-Box"));

        var page = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter("lab"), 1, 10);

        page.Items.Select(r => r.DeviceHost).Should().Equal("192.0.2.2");
    }

    [Fact]
    public async Task DeviceFilter_IsExactAndCaseInsensitive_AndCombinesWithSearch()
    {
        await SeedAsync(
            Result("nas.lan", 0, "NAS"),
            Result("NAS.lan", 1, "NAS"),
            Result("nas.lan.example", 2, "NAS"),
            Result("nas.lan", 3, "Other"));

        var byHost = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter(DeviceHost: "nas.lan"), 1, 10);
        var both = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter("other", "nas.lan"), 1, 10);

        byHost.Total.Should().Be(3);
        both.Items.Select(r => r.DeviceName).Should().Equal("Other");
    }

    [Fact]
    public async Task Index_IsThePositionInTheFilteredOrder()
    {
        await SeedAsync(Enumerable.Range(0, 15).Select(i => Result(i % 2 == 0 ? "192.0.2.1" : "192.0.2.2", i)).ToArray());
        var target = await _context.Iperf3Results.SingleAsync(r => r.TestTime == T0.AddMinutes(4));

        var unfiltered = await _repository.GetLanHistoryIndexAsync(new SpeedTestHistoryFilter(), target.Id);
        var filtered = await _repository.GetLanHistoryIndexAsync(new SpeedTestHistoryFilter(DeviceHost: "192.0.2.1"), target.Id);
        var excluded = await _repository.GetLanHistoryIndexAsync(new SpeedTestHistoryFilter(DeviceHost: "192.0.2.2"), target.Id);

        unfiltered.Should().Be(10); // minutes 14..5 are ahead of it
        filtered.Should().Be(5);    // minutes 14, 12, 10, 8, 6
        excluded.Should().BeNull();
    }

    [Fact]
    public async Task Index_BreaksTimeTiesById_LikeThePages()
    {
        await SeedAsync(Result("192.0.2.1", 0), Result("192.0.2.2", 0), Result("192.0.2.3", 0));
        var page = await _repository.GetLanHistoryPageAsync(new SpeedTestHistoryFilter(), 1, 10);

        for (var i = 0; i < page.Items.Count; i++)
            (await _repository.GetLanHistoryIndexAsync(new SpeedTestHistoryFilter(), page.Items[i].Id)).Should().Be(i);
    }

    [Fact]
    public async Task ClientScope_PagesOnlyClientDirections()
    {
        await SeedAsync(
            Result("192.0.2.1", 0),
            Result("192.0.2.2", 1, direction: SpeedTestDirection.ClientToServer),
            Result("192.0.2.3", 2, direction: SpeedTestDirection.BrowserToServer),
            Result("wan", 3, direction: SpeedTestDirection.OpenSpeedTestWan));
        var scope = _context.Iperf3Results.Where(r => Iperf3ResultQueries.ClientDirections.Contains(r.Direction));

        var page = await scope.PageAsync(_context, new SpeedTestHistoryFilter(), 1, 10);

        page.Items.Select(r => r.DeviceHost).Should().Equal("192.0.2.3", "192.0.2.2");
        page.Items.Should().OnlyContain(r => r.RawDownloadJson == null);
    }

    [Fact]
    public async Task Stamp_ChangesOnAddAndDelete_AndIsNoneWhenEmpty()
    {
        var scope = _context.Iperf3Results.Where(r => r.Direction == SpeedTestDirection.OpenSpeedTestWan);
        (await scope.StampAsync()).Should().Be(SpeedTestResultsStamp.None);

        await SeedAsync(
            Result("192.0.2.1", 0, direction: SpeedTestDirection.OpenSpeedTestWan),
            Result("192.0.2.2", 5, direction: SpeedTestDirection.OpenSpeedTestWan),
            Result("192.0.2.3", 9)); // outside the scope
        var before = await scope.StampAsync();

        before.Count.Should().Be(2);
        before.NewestTestTime.Should().Be(T0.AddMinutes(5));
        (await scope.StampAsync()).Should().Be(before);

        var newest = await _context.Iperf3Results.SingleAsync(r => r.Id == before.MaxId);
        _context.Iperf3Results.Remove(newest);
        await _context.SaveChangesAsync();

        var after = await scope.StampAsync();
        after.Should().NotBe(before);
        after.Count.Should().Be(1);
    }

    [Fact]
    public async Task WanResultSources_TranslateToADistinctSet()
    {
        await SeedAsync(
            Result("wan", 0, direction: SpeedTestDirection.UwnWan),
            Result("wan", 1, direction: SpeedTestDirection.UwnWan),
            Result("wan", 2, direction: SpeedTestDirection.UwnWan));
        var rows = await _context.Iperf3Results.ToListAsync();
        rows[0].WanNetworkGroup = "WAN"; rows[0].WanName = "Fiber";
        rows[1].WanNetworkGroup = "WAN"; rows[1].WanName = "Fiber";
        rows[2].WanNetworkGroup = "WAN2"; rows[2].WanName = null; rows[2].Success = false;
        await _context.SaveChangesAsync();

        // The same query shape the WAN test services run.
        var sources = await _context.Iperf3Results.AsNoTracking()
            .Select(r => new WanResultSource(r.WanNetworkGroup, r.WanName, r.Success))
            .Distinct()
            .ToListAsync();

        sources.Should().BeEquivalentTo(new[]
        {
            new WanResultSource("WAN", "Fiber", true),
            new WanResultSource("WAN2", null, false),
        });
    }

    [Fact]
    public async Task LanResults_HonorWindowAndDirection()
    {
        await SeedAsync(
            Result("192.0.2.1", 0),
            Result("192.0.2.2", 60),
            Result("192.0.2.3", 61, direction: SpeedTestDirection.ClientToServer),
            Result("wan", 62, direction: SpeedTestDirection.UwnWan));

        var window = await _repository.GetLanResultsAsync(T0.AddMinutes(30));
        var serverOnly = await _repository.GetLanResultsAsync(null, SpeedTestDirection.ServerToDevice);

        window.Select(r => r.DeviceHost).Should().Equal("192.0.2.3", "192.0.2.2");
        serverOnly.Select(r => r.DeviceHost).Should().Equal("192.0.2.2", "192.0.2.1");
        window.Should().OnlyContain(r => r.RawDownloadJson == null && r.RawUploadJson == null);
    }
}
