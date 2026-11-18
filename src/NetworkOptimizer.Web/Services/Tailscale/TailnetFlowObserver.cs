using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>
/// Reads how a Tailscale device connects off the gateway's own flows, with no Tailscale access on any
/// host: a flow to one of its endpoints that carries traffic is a direct path; with none, a DERP relay
/// connection that is moving traffic is a relayed one. The gateway agent sends flows only under a Live
/// Traffic lease, so this holds one for a few seconds, as a viewer would, and reads nothing on a site
/// that turned Live Traffic off. What it returns is observed, never guessed: no matching flow, no path.
/// </summary>
public sealed class TailnetFlowObserver(LiveTrafficRegistry liveTraffic, AgentTunnelRegistry tunnels, TailscaleDirectoryRegistry tailscale,
    ISystemSettingsService settings)
{
    /// <summary>Each read is its own viewer, so two at once never end each other's lease.</summary>
    private static string NewViewerId() => "tailscale-path-" + Guid.NewGuid().ToString("N")[..8];
    private static readonly TimeSpan Watch = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    /// <summary>A DERP connection idles with keepalives; below this rate it relays nothing.</summary>
    private const double RelayBusyBps = 8_000;

    /// <summary>The device's path as the gateway sees it now; null when it cannot tell.</summary>
    public async Task<TailnetPathInfo?> ObserveAsync(string slug, string peerIp, CancellationToken ct)
    {
        var snapshot = tailscale.Get(slug);
        if (snapshot.Peer(peerIp) is not { } peer) return null;
        var agents = tunnels.GetForSite(slug).Where(c => c.HasCapability(AgentTunnelConnection.LiveTrafficCapability)).ToList();
        if (agents.Count == 0) return null;
        if (await liveTraffic.IsTurnedOffAsync(settings, slug)) return null;

        var store = liveTraffic.GetFor(slug);
        store.SetTailnet(snapshot, connected: true);
        var viewerId = NewViewerId();
        var deadline = DateTime.UtcNow + Watch;
        try
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                if (store.TouchViewer(viewerId, now))
                {
                    foreach (var agent in agents)
                        agent.TrySend(new ServerMessage
                        {
                            LiveTrafficConfig = new LiveTrafficConfig { LeaseSeconds = (int)LiveTrafficStore.LeaseLength.TotalSeconds, MaxFlows = 500 },
                        });
                    store.NoteLeasePushed(now);
                }

                var flows = store.TailnetFlows();
                if (Direct(flows, peer.Name, now) is { } direct) return direct;
                if (now >= deadline) return Relayed(flows, peer.Name, snapshot, now);
                await Task.Delay(Poll, ct);
            }
        }
        finally
        {
            // Nobody is watching a path read, so its lease ends now rather than running out.
            if (store.ReleaseViewer(viewerId, DateTime.UtcNow))
            {
                foreach (var agent in agents)
                    agent.TrySend(new ServerMessage { LiveTrafficConfig = new LiveTrafficConfig { LeaseSeconds = 0 } });
                store.EndLease();
            }
        }
    }

    internal static TailnetPathInfo? Direct(IReadOnlyList<LiveFlowRow> flows, string peer, DateTime now) =>
        flows.FirstOrDefault(f => f.TailnetEndpointPeer == peer && f.TailnetRole == "Direct") is { } row
            ? new TailnetPathInfo { Kind = TailnetPathKind.Direct, Endpoint = $"{row.DestinationIp}:{row.RemotePort}", ReadAt = now, Observed = true }
            : null;

    /// <summary>Relayed only when nothing went to the device's endpoints and a DERP relay connection was busy.</summary>
    internal static TailnetPathInfo? Relayed(IReadOnlyList<LiveFlowRow> flows, string peer, TailnetSnapshot snapshot, DateTime now)
    {
        if (flows.Any(f => f.TailnetEndpointPeer == peer && f.TotalDownBytes + f.TotalUpBytes >= LiveTrafficStore.TailnetDirectMinBytes))
            return null;
        var relay = flows.Where(f => f.TailnetRole == "DERP relay" && f.DownBps + f.UpBps >= RelayBusyBps)
            .MaxBy(f => f.DownBps + f.UpBps);
        if (relay == null) return null;
        var region = snapshot.DerpAt(relay.DestinationIp);
        return new TailnetPathInfo { Kind = TailnetPathKind.Derp, Relay = region?.Code, RelayName = region?.Name, ReadAt = now, Observed = true };
    }
}
