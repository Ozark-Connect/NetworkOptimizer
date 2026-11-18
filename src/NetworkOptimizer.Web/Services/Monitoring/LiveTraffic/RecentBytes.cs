namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// One flow's bytes over the last <see cref="Window"/>, for the Recent Down and Recent Up columns. A flow
/// younger than the window, or a capture that started inside it, counts from its start. Only windows that
/// moved bytes are kept, so an idle flow holds nothing.
/// </summary>
internal sealed class RecentBytes
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly Queue<(DateTime At, long Down, long Up)> _windows = new();

    /// <summary>Bytes toward the client in the window.</summary>
    public long Down { get; private set; }

    /// <summary>Bytes from the client in the window.</summary>
    public long Up { get; private set; }

    /// <summary>Counts one agent window's bytes, moved by <paramref name="at"/>.</summary>
    public void Add(DateTime at, long down, long up)
    {
        if (down <= 0 && up <= 0) return;
        _windows.Enqueue((at, Math.Max(0, down), Math.Max(0, up)));
        Down += Math.Max(0, down);
        Up += Math.Max(0, up);
    }

    /// <summary>Drops the windows older than <see cref="Window"/>.</summary>
    public void Prune(DateTime now)
    {
        while (_windows.TryPeek(out var oldest) && now - oldest.At > Window)
        {
            _windows.Dequeue();
            Down -= oldest.Down;
            Up -= oldest.Up;
        }
    }
}
