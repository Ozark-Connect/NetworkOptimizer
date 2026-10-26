namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>What one <see cref="SustainGate.Observe"/> call changed.</summary>
internal enum GateTransition { None, Opened, Closed }

/// <summary>
/// Two-sided sustain. A condition must hold for the window to open an alert, and its clear
/// condition must hold just as long to close one, so a value hovering at the bar produces one
/// alert and one recovery rather than a stream of both.
/// </summary>
internal sealed class SustainGate
{
    private DateTime? _raiseSince;
    private DateTime? _clearSince;

    public bool Open { get; private set; }

    public GateTransition Observe(bool raise, bool clear, DateTime now, TimeSpan window)
    {
        _raiseSince = raise ? _raiseSince ?? now : null;
        _clearSince = clear ? _clearSince ?? now : null;

        if (!Open && _raiseSince is { } raiseSince && now - raiseSince >= window)
        {
            Open = true;
            _clearSince = null;
            return GateTransition.Opened;
        }

        if (Open && _clearSince is { } clearSince && now - clearSince >= window)
        {
            Open = false;
            _raiseSince = null;
            return GateTransition.Closed;
        }

        return GateTransition.None;
    }

    /// <summary>Starts the gate open, for an alert still open before a restart, so its clear is published.</summary>
    public void MarkOpen() => Open = true;

    /// <summary>
    /// Discards both pending runs without changing whether the alert is open, for a poll whose
    /// reading says nothing either way.
    /// </summary>
    public void Stall()
    {
        _raiseSince = null;
        _clearSince = null;
    }
}
