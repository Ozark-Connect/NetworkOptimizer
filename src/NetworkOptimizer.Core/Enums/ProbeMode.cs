namespace NetworkOptimizer.Core.Enums;

/// <summary>
/// How a probe contacts its target. Shared between the probe-execution layer and the
/// MonitoringTarget storage model so a target's last-known mode round-trips through the
/// database without conversion.
///
/// Integer values are persisted in SQLite — do not renumber existing members.
/// </summary>
public enum ProbeMode
{
    Icmp = 0,
    Tcp = 1,
    Udp = 2
}
