namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Wired clients seen on a different switch port than at the last topology read, for MAC Flapping. In memory and
/// bounded to the last two days: a restart starts it empty, and the detector needs three moves in its window.
/// </summary>
public sealed class MacMoveTracker
{
    private static readonly TimeSpan Keep = TimeSpan.FromHours(48);
    private readonly Dictionary<string, string> _lastPort = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTime At, string Mac, string Name, string From, string To)> _moves = [];
    private readonly object _lock = new();

    /// <summary>One topology read: each wired client with the port it sits on, by its on-screen label.</summary>
    public void Observe(DateTime now, IEnumerable<(string Mac, string Name, string Port)> wired)
    {
        lock (_lock)
        {
            foreach (var (mac, name, port) in wired)
            {
                var key = LanLinkBuilder.Mac(mac);
                if (key.Length == 0 || string.IsNullOrEmpty(port)) continue;
                if (_lastPort.TryGetValue(key, out var was) && was != port) _moves.Add((now, key, name, was, port));
                _lastPort[key] = port;
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