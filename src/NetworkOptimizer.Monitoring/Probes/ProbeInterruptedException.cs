namespace NetworkOptimizer.Monitoring.Probes;

/// <summary>
/// Thrown when the ping binary was ended by a signal from outside the probe (an agent's service stopping
/// takes its children with it). The burst was cut short, so its missing replies are not loss: callers
/// drop the sample entirely, as for <see cref="ProbeOutputTimeoutException"/>.
/// </summary>
public sealed class ProbeInterruptedException : Exception
{
    public ProbeInterruptedException(string message) : base(message)
    {
    }
}
