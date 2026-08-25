using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Source selection and the roam follow, end to end over a fake fleet.
///
/// The rule every test here is really checking is the same one: the AP Agent path is an optional
/// accelerator, so anything it cannot answer must come back as null and send the caller to the
/// console path, never as an error and never as a gap.
/// </summary>
public class ApAgentClientLiveServiceTests
{
    private const string Site = "test-site";
    private const string ApOne = "aa:bb:cc:11:22:01";
    private const string ApTwo = "aa:bb:cc:33:44:02";
    private const string ApThree = "aa:bb:cc:55:66:03";
    private const string ClientMac = "aa:bb:cc:dd:ee:ff";
    private const string MldMac = "aa:bb:cc:dd:ee:f0";

    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentClientLiveService Service(FakeReader reader)
        => new(reader, NullLogger<ApAgentClientLiveService>.Instance);

    private static ApAgentClient Client(string mac, int signal = -55, string band = "5")
        => new()
        {
            Key = mac,
            Mac = mac,
            Band = band,
            Channel = 44,
            Bandwidth = 80,
            Signal = signal,
            TxRateKbps = 800_000,
            RxRateKbps = 600_000,
            Links = { new ApAgentClientLink { Mac = mac, Active = true, Band = band, Signal = signal } },
        };

    [Fact]
    public async Task A_site_with_no_ap_agents_makes_no_agent_request_at_all()
    {
        var reader = new FakeReader();
        var follower = new ApAgentRoamFollower();

        var result = await Service(reader).PollAsync(Site, ClientMac, ApOne, follower, Now);

        result.Should().BeNull("the console path is what answers on a site without AP Agents");
        reader.ClientReads.Should().BeEmpty();
        follower.State.Should().Be(ApAgentFollowState.Lost);
    }

    [Fact]
    public async Task An_access_point_without_an_agent_keeps_its_client_on_the_console_path()
    {
        // ApTwo is enrolled; the client is on ApOne, which is not.
        var reader = new FakeReader();
        reader.Place(ApTwo, Client("00:11:22:33:44:55"));

        var result = await Service(reader).PollAsync(Site, ClientMac, ApOne, new ApAgentRoamFollower(), Now);

        result.Should().BeNull();
        reader.ClientReads.Should().BeEmpty("an access point with no agent is never asked");
    }

    [Fact]
    public async Task The_client_is_read_from_the_access_point_it_is_on()
    {
        var reader = new FakeReader();
        reader.Place(ApOne, Client(ClientMac, signal: -47));
        reader.Aps.Add(ApTwo);
        var follower = new ApAgentRoamFollower();

        var result = await Service(reader).PollAsync(Site, ClientMac, ApOne, follower, Now);

        result.Should().NotBeNull();
        result!.ApMac.Should().Be(ApOne);
        result.Client.Signal.Should().Be(-47);
        reader.ClientReads.Should().ContainSingle().Which.Should().Be(ApOne);
        follower.State.Should().Be(ApAgentFollowState.Attached);
    }

    [Fact]
    public async Task A_wifi7_client_is_one_client_keyed_on_its_mld_mac()
    {
        var mlo = new ApAgentClient
        {
            Key = MldMac,
            Mac = MldMac,
            MldMac = MldMac,
            IsMlo = true,
            Band = "6",
            Channel = 37,
            Bandwidth = 160,
            Signal = -52,
            Links =
            {
                new ApAgentClientLink { Mac = "aa:bb:cc:dd:ee:f1", Band = "5", Active = false, Signal = -91 },
                new ApAgentClientLink { Mac = "aa:bb:cc:dd:ee:f2", Band = "6", Active = true, Signal = -52 },
            },
        };

        var reader = new FakeReader();
        reader.Place(ApOne, mlo);

        // The page holds one of the link MACs, which is what the console reported.
        var result = await Service(reader).PollAsync(Site, "aa:bb:cc:dd:ee:f1", ApOne, new ApAgentRoamFollower(), Now);

        result.Should().NotBeNull();
        result!.Client.Key.Should().Be(MldMac, "a Wi-Fi 7 client is one record, not one per link");
        result.Client.Links.Should().HaveCount(2);
        result.Client.Signal.Should().Be(-52, "the scalars describe the active link");
    }

    [Fact]
    public async Task A_roam_is_followed_to_the_new_access_point()
    {
        var reader = new FakeReader();
        reader.Place(ApOne, Client(ClientMac));
        reader.Aps.Add(ApTwo);
        reader.Aps.Add(ApThree);
        var service = Service(reader);
        var follower = new ApAgentRoamFollower();

        (await service.PollAsync(Site, ClientMac, ApOne, follower, Now))!.ApMac.Should().Be(ApOne);

        // The client walks: ApOne no longer holds it, ApThree does.
        reader.Clear(ApOne);
        reader.Place(ApThree, Client(ClientMac, signal: -63));

        var settled = await service.PollAsync(Site, ClientMac, ApOne, follower, Now.AddSeconds(1));

        settled.Should().NotBeNull("the access point that took the client is the authority on holding it");
        settled!.ApMac.Should().Be(ApThree);
        settled.Client.Signal.Should().Be(-63);
        follower.CurrentAp.Should().Be(ApThree);
    }

    [Fact]
    public async Task The_announced_peer_is_asked_first_on_a_roam()
    {
        var reader = new FakeReader();
        reader.Place(ApOne, Client(ClientMac));
        reader.Aps.Add(ApTwo);
        reader.Aps.Add(ApThree);
        reader.PeerHints[ApOne] = "aa:bb:cc:55:66:1a"; // ApThree's radio, last octet aside
        var service = Service(reader);
        var follower = new ApAgentRoamFollower();

        await service.PollAsync(Site, ClientMac, ApOne, follower, Now);
        reader.Clear(ApOne);
        reader.Place(ApThree, Client(ClientMac));
        reader.ClientReads.Clear();

        await service.PollAsync(Site, ClientMac, ApOne, follower, Now.AddSeconds(1));

        reader.ClientReads.Should().Contain(ApThree);
        reader.ClientReads.SkipWhile(ap => ap == ApOne).First().Should().Be(ApThree);
    }

    [Fact]
    public async Task A_client_that_never_reappears_stops_costing_requests()
    {
        var reader = new FakeReader();
        reader.Place(ApOne, Client(ClientMac));
        reader.Aps.Add(ApTwo);
        reader.Aps.Add(ApThree);
        var service = Service(reader);
        var follower = new ApAgentRoamFollower();

        await service.PollAsync(Site, ClientMac, ApOne, follower, Now);
        reader.Clear(ApOne);

        // Two ticks a second for the whole window, then one past it.
        for (var i = 1; i <= 24; i++)
            (await service.PollAsync(Site, ClientMac, ApOne, follower, Now.AddSeconds(i * 0.5))).Should().BeNull();

        var afterWindow = Now + ApAgentRoamFollower.SearchWindow + TimeSpan.FromSeconds(2);
        (await service.PollAsync(Site, ClientMac, ApOne, follower, afterWindow)).Should().BeNull();

        reader.ClientReads.Clear();
        (await service.PollAsync(Site, ClientMac, ApOne, follower, afterWindow.AddSeconds(1))).Should().BeNull();
        reader.ClientReads.Should().BeEmpty("a client that walked out of the building stops the fan-out");
        follower.State.Should().Be(ApAgentFollowState.Lost);
    }

    [Fact]
    public async Task An_agent_that_goes_away_mid_session_falls_back_without_searching_the_site()
    {
        var reader = new FakeReader();
        reader.Place(ApOne, Client(ClientMac));
        reader.Aps.Add(ApTwo);
        reader.Aps.Add(ApThree);
        var service = Service(reader);
        var follower = new ApAgentRoamFollower();

        await service.PollAsync(Site, ClientMac, ApOne, follower, Now);

        reader.Unreachable.Add(ApOne);
        reader.ClientReads.Clear();

        var result = await service.PollAsync(Site, ClientMac, ApOne, follower, Now.AddSeconds(1));

        result.Should().BeNull("the caller polls WiFiman for this tick");
        follower.State.Should().Be(ApAgentFollowState.Lost);
        reader.ClientReads.Should().ContainSingle().Which.Should().Be(ApOne);
        reader.PeerHintReads.Should().BeEmpty("an unreachable agent is a fault, not a roam");
    }

    [Fact]
    public async Task An_agent_that_comes_back_is_picked_up_again()
    {
        var reader = new FakeReader();
        reader.Place(ApOne, Client(ClientMac));
        var service = Service(reader);
        var follower = new ApAgentRoamFollower();

        await service.PollAsync(Site, ClientMac, ApOne, follower, Now);
        reader.Unreachable.Add(ApOne);
        await service.PollAsync(Site, ClientMac, ApOne, follower, Now.AddSeconds(1));
        reader.Unreachable.Clear();

        var result = await service.PollAsync(Site, ClientMac, ApOne, follower, Now.AddSeconds(2));

        result.Should().NotBeNull();
        result!.ApMac.Should().Be(ApOne);
    }

    /// <summary>
    /// A fleet of access points, each holding whichever clients the test put on it. Resolves a link
    /// MAC to its parent client the way the agent's own /client lookup does.
    /// </summary>
    private sealed class FakeReader : IApAgentClientReader
    {
        public HashSet<string> Aps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unreachable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PeerHints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ClientReads { get; } = new();
        public List<string> PeerHintReads { get; } = new();

        private readonly Dictionary<string, List<ApAgentClient>> _held = new(StringComparer.OrdinalIgnoreCase);

        public void Place(string apMac, ApAgentClient client)
        {
            Aps.Add(apMac);
            if (!_held.TryGetValue(apMac, out var list)) _held[apMac] = list = new List<ApAgentClient>();
            list.Add(client);
        }

        public void Clear(string apMac) => _held.Remove(apMac);

        public Task<IReadOnlyList<string>> ListAgentApsAsync(string siteSlug, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Aps.OrderBy(a => a, StringComparer.Ordinal).ToList());

        public Task<ApAgentClientLookup> ReadClientAsync(
            string siteSlug, string apMac, string clientMac, CancellationToken ct = default)
        {
            ClientReads.Add(apMac);
            if (Unreachable.Contains(apMac))
                return Task.FromResult(new ApAgentClientLookup(ApAgentClientLookupStatus.Unreachable, null));

            var found = _held.TryGetValue(apMac, out var list) ? Find(list, clientMac) : null;
            return Task.FromResult(found == null
                ? new ApAgentClientLookup(ApAgentClientLookupStatus.NotOnAp, null)
                : new ApAgentClientLookup(ApAgentClientLookupStatus.Found,
                    new ApAgentClientPayload { Client = found }));
        }

        public Task<string?> ReadPeerHintAsync(
            string siteSlug, string apMac, string clientMac, DateTime sinceUtc, CancellationToken ct = default)
        {
            PeerHintReads.Add(apMac);
            return Task.FromResult(PeerHints.TryGetValue(apMac, out var hint) ? hint : null);
        }

        private static ApAgentClient? Find(List<ApAgentClient> clients, string mac)
            => clients.FirstOrDefault(c =>
                   c.Key.Equals(mac, StringComparison.OrdinalIgnoreCase)
                   || c.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase))
               ?? clients.FirstOrDefault(c => c.Links.Any(l =>
                   string.Equals(l.Mac, mac, StringComparison.OrdinalIgnoreCase)));
    }
}
