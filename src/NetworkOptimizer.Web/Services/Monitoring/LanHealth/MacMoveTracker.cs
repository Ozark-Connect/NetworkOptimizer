namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Wired clients seen on a different switch port than at the last topology read, for MAC Flapping. In memory and
/// bounded to the last two days: a restart starts it empty, and the detector needs three moves in its window.
/// </summary>
public sealed class MacMoveTracker
{
    private static readonly TimeSpan Keep = TimeSpan.FromHours(48);
    private readonly Dictionary<string, (string Port, IReadOnlyCollection<string> Path)> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTime At, string Mac, string Name, string From, string To)> _moves = [];
    private readonly object _lock = new();

    /// <summary>
    /// One topology read: each wired client with the port it sits on, by its on-screen label, and the uplink ports
    /// above that port's device. The UniFi Console sometimes reports a client on an uplink on its own path, so a
    /// sighting on that path is the same attachment, not a move.
    /// </summary>
    public void Observe(DateTime now, IEnumerable<(string Mac, string Name, string Port, IReadOnlyCollection<string> Path)> wired)
    {
        lock (_lock)
        {
            foreach (var (mac, name, port, path) in wired)
            {
                var key = LanLinkBuilder.Mac(mac);
                if (key.Length == 0 || string.IsNullOrEmpty(port)) continue;
                if (_last.TryGetValue(key, out var was))
                {
                    if (was.Port == port) continue;
                    // Seen on an uplink above the last port: keep the more specific port.
                    if (was.Path.Contains(port)) continue;
                    // The last sighting was an uplink above this port: this is where it sits, not a move.
                    if (!path.Contains(was.Port)) _moves.Add((now, key, name, was.Port, port));
                }
                _last[key] = (port, path);
            }
            _moves.RemoveAll(m => m.At < now - Keep);
        }
    }

    /// <summary>Per client with moves since <paramref name="from"/>: its name, how many, and the ports it moved between.</summary>
    public List<(string Mac, string Name, int Moves, IReadOnlyList<string> Ports)> Since(DateTime from)
    {
        lock (_lock)
            return _moves.Where(m => m.At >= from).GroupBy(m => m.Mac)
                .Select(g => (g.Key, g.Last().Name, g.Count(), (IReadOnlyList<string>)g.SelectMany(m => new[] { m.From, m.To }).Distinct().ToList()))
                .ToList();
    }
}
