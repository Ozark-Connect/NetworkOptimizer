namespace NetworkOptimizer.Web.Services;

/// <summary>Which Dashboard speed test card a saved result belongs to.</summary>
public enum SpeedTestResultKind
{
    Lan,
    Wan
}

/// <summary>
/// App-wide broadcast that a speed test result was saved or enriched, so live UI (the Dashboard's
/// LAN Speed Test and WAN Speed Test cards) reloads instead of waiting for a page load. Carries no
/// result data: subscribers re-read through their own gated services. Not the alert bus, which is
/// single-reader: a second consumer there would take events away from alert processing.
/// A singleton because publishers and subscribers live in different circuits. Handlers run on the
/// publisher's thread, so subscribers marshal with InvokeAsync and never throw back into the save.
/// </summary>
public class SpeedTestResultNotifier
{
    /// <summary>Raised with the site slug and the kind of result saved.</summary>
    public event Action<string, SpeedTestResultKind>? ResultSaved;

    /// <summary>Raises <see cref="ResultSaved"/>. A throwing subscriber never fails the caller's save.</summary>
    public void NotifyResultSaved(string siteSlug, SpeedTestResultKind kind)
    {
        var handlers = ResultSaved;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string, SpeedTestResultKind>>())
        {
            try { handler(siteSlug, kind); }
            catch { /* a subscriber's failure is its own */ }
        }
    }
}
