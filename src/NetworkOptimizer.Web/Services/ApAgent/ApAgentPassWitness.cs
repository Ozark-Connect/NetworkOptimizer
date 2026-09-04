using System.Text;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Records which access points claimed each client in one pass, so a client claimed by more than
/// one can be reported.
///
/// One client can only be associated to one access point. When several report the same client we
/// write a point per access point and the map redraws it onto whichever answered last, which is
/// seen as a client flickering on and off. It is transient - it appears while a device is waking
/// and attempting associations, and is gone by the time anyone looks - so it is captured as it
/// happens rather than hunted afterwards.
/// </summary>
public sealed class ApAgentPassWitness
{
    private readonly Dictionary<string, List<Claim>> _claims = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private readonly record struct Claim(string ApMac, long? IdleSeconds, double? SignalDbm, bool Authorized, string? Band);

    /// <summary>Notes that this access point reported this client in the current pass.</summary>
    public void Claimed(string clientMac, string apMac, long? idleSeconds, double? signalDbm, bool authorized, string? band)
    {
        if (string.IsNullOrEmpty(clientMac)) return;

        lock (_lock)
        {
            if (!_claims.TryGetValue(clientMac, out var list))
                _claims[clientMac] = list = new List<Claim>(1);
            list.Add(new Claim(apMac, idleSeconds, signalDbm, authorized, band));
        }
    }

    /// <summary>
    /// Describes every client more than one access point claimed, one line each, or an empty list
    /// when the pass was clean.
    /// </summary>
    public IReadOnlyList<string> Contested()
    {
        lock (_lock)
        {
            var lines = new List<string>();
            foreach (var (mac, claims) in _claims)
            {
                if (claims.Select(c => c.ApMac).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;

                var sb = new StringBuilder(mac).Append(": ");
                sb.AppendJoin(", ", claims.Select(c =>
                    $"{c.ApMac}[{c.Band ?? "?"} rssi={c.SignalDbm?.ToString("0.#") ?? "?"} idle={c.IdleSeconds?.ToString() ?? "?"} auth={c.Authorized}]"));
                lines.Add(sb.ToString());
            }
            return lines;
        }
    }

    public void Reset()
    {
        lock (_lock) _claims.Clear();
    }
}
