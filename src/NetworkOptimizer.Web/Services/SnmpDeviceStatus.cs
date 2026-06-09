using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Where a device sits in the SNMP polling lifecycle, as seen by
/// <see cref="MonitoringCollectionAgent"/>. Surfaced to the Monitoring → Setup
/// dashboard so users can tell at a glance which devices are actually feeding data.
/// </summary>
public enum SnmpPollState
{
    /// <summary>
    /// We can't confirm SNMP is enabled on the device: UniFi reports no
    /// snmp_location or snmp_contact, which is the only signal we have, so the
    /// agent doesn't poll it. SNMP may still be on - we just can't tell.
    /// </summary>
    SnmpDisabled,

    /// <summary>
    /// SNMP is enabled and the device isn't excluded, but the agent hasn't yet
    /// recorded a successful poll this app lifecycle (e.g. monitoring just started,
    /// or the first cycle hasn't completed).
    /// </summary>
    NotYetPolled,

    /// <summary>The agent has successfully polled this device at least once and isn't excluding it.</summary>
    Polling,

    /// <summary>
    /// SNMP is enabled but the device was dropped from polling after repeated
    /// failures. It will be retried automatically when the exclusion expires.
    /// </summary>
    Excluded
}

/// <summary>
/// A point-in-time snapshot of one device's SNMP polling status, assembled by
/// <see cref="MonitoringCollectionAgent.GetSnmpDeviceStatusesAsync"/> for the
/// Monitoring → Setup device dashboard.
/// </summary>
/// <param name="Mac">Device MAC (colon-delimited, lowercase).</param>
/// <param name="Name">Display name (falls back to MAC when UniFi reports no name).</param>
/// <param name="DeviceType">UniFi device classification (gateway, switch, AP, ...).</param>
/// <param name="SnmpEnabled">Whether we believe SNMP is enabled on the device (snmp_location or snmp_contact set).</param>
/// <param name="PollState">Where the device sits in the polling lifecycle.</param>
/// <param name="LastPolledUtc">Last successful SNMP poll (UTC), or null if never polled this lifecycle.</param>
/// <param name="FailureCount">Consecutive SNMP failures recorded since the last success.</param>
/// <param name="ExcludedAtUtc">When the device was excluded from polling (UTC), or null if not excluded.</param>
public sealed record SnmpDeviceStatus(
    string Mac,
    string Name,
    DeviceType DeviceType,
    bool SnmpEnabled,
    SnmpPollState PollState,
    DateTime? LastPolledUtc,
    int FailureCount,
    DateTime? ExcludedAtUtc);
