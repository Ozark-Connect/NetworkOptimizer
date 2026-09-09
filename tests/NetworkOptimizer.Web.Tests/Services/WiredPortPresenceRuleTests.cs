using NetworkOptimizer.Web.Services.WiredPortPresence;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class WiredPortPresenceRuleTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 17, 0, 0, DateTimeKind.Utc);
    private const string Switch = "aa:bb:cc:dd:ee:01";
    private const string Client = "00:11:22:33:44:55";

    private static PortSighting Sighting(string client = Client, int port = 1, TimeSpan? ago = null, string sw = Switch) =>
        new(sw, port, client, Now - (ago ?? TimeSpan.FromHours(2)), "192.0.2.10", "Server1");

    private static IReadOnlyList<WiredPortPresence> Run(
        IReadOnlyList<PortSighting> sightings,
        IEnumerable<string>? listed = null,
        IEnumerable<(string, int)>? occupied = null,
        Func<string, int, bool?>? portUp = null,
        IEnumerable<(string, int)>? uplinks = null,
        Func<string, int, bool?>? transmitting = null) =>
        WiredPortPresenceRule.Evaluate(
            sightings,
            new HashSet<string>(listed ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            new HashSet<(string, int)>(occupied ?? Array.Empty<(string, int)>()),
            portUp ?? ((_, _) => true),
            transmitting ?? ((_, _) => true),
            new HashSet<(string, int)>(uplinks ?? Array.Empty<(string, int)>()),
            Now);

    [Fact]
    public void ALinkedPortWithASilentHostIsNotPresence()
    {
        // A sleeping NIC keeps its link; only the host's own traffic says it is awake.
        Assert.Empty(Run(new[] { Sighting() }, transmitting: (_, _) => false));
    }

    [Fact]
    public void APortWithNoCountersIsNotPresence()
    {
        Assert.Empty(Run(new[] { Sighting() }, transmitting: (_, _) => null));
    }

    [Fact]
    public void ADroppedClientOnAnUpPortIsPresent()
    {
        var result = Run(new[] { Sighting() });

        var p = Assert.Single(result);
        Assert.Equal(Client, p.ClientMac);
        Assert.Equal(Switch, p.SwitchMac);
        Assert.Equal(1, p.Port);
        Assert.Equal("192.0.2.10", p.Ip);
        Assert.Equal("Server1", p.Name);
    }

    [Fact]
    public void AListedClientIsLeftToTheConsole()
    {
        Assert.Empty(Run(new[] { Sighting() }, listed: new[] { Client }));
    }

    [Fact]
    public void ADownPortIsNotPresence()
    {
        Assert.Empty(Run(new[] { Sighting() }, portUp: (_, _) => false));
    }

    [Fact]
    public void AnUnknownPortStateIsNotPresence()
    {
        Assert.Empty(Run(new[] { Sighting() }, portUp: (_, _) => null));
    }

    [Fact]
    public void APortAnotherListedClientOccupiesIsNotPresence()
    {
        Assert.Empty(Run(new[] { Sighting() }, occupied: new[] { (Switch, 1) }));
    }

    [Fact]
    public void APortThatChangedHandsSinceIsNotPresence()
    {
        var sightings = new[]
        {
            Sighting(ago: TimeSpan.FromDays(3)),
            Sighting(client: "00:11:22:33:44:66", ago: TimeSpan.FromDays(1)),
        };
        // Only the newer occupant can be present; the older placement was superseded.
        var result = Run(sightings);
        var p = Assert.Single(result);
        Assert.Equal("00:11:22:33:44:66", p.ClientMac);
    }

    [Fact]
    public void AnUplinkPortIsNotPresence()
    {
        Assert.Empty(Run(new[] { Sighting() }, uplinks: new[] { (Switch, 1) }));
    }

    [Fact]
    public void APlacementOlderThanTheLookbackIsNotPresence()
    {
        Assert.Empty(Run(new[] { Sighting(ago: WiredPortPresenceRule.Lookback + TimeSpan.FromHours(1)) }));
    }

    [Fact]
    public void AClientMovedBetweenPortsIsPresentOnItsNewestOne()
    {
        var sightings = new[]
        {
            Sighting(port: 3, ago: TimeSpan.FromDays(2)),
            Sighting(port: 1, ago: TimeSpan.FromHours(1)),
        };
        var p = Assert.Single(Run(sightings));
        Assert.Equal(1, p.Port);
    }
}
