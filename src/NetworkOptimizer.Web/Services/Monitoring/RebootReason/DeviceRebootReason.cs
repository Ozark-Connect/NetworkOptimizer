namespace NetworkOptimizer.Web.Services.Monitoring.RebootReason;

/// <summary>
/// How a device's previous run ended, in decreasing order of "the operator needs to know".
/// </summary>
public enum RebootCategory
{
    /// <summary>Nothing conclusive was found.</summary>
    Unknown = 0,

    /// <summary>Firmware was upgraded and the device restarted to run the new image.</summary>
    FirmwareUpgrade,

    /// <summary>An orderly, kernel-initiated restart (admin action, provisioning, config apply).</summary>
    CommandedReboot,

    /// <summary>Power was removed: the SoC reset register or an outlet event says so.</summary>
    PowerLoss,

    /// <summary>The previous run stopped mid-flight with no shutdown recorded - power loss or a hard hang.</summary>
    AbruptStop,

    /// <summary>The kernel panicked or oopsed; a crash trace survived in pstore.</summary>
    KernelPanic,

    /// <summary>An internal bus or SoC hang, distinguished by the platform's reset register.</summary>
    HardwareHang,

    /// <summary>The watchdog fired and reset the device.</summary>
    Watchdog,

    /// <summary>A smart outlet or PoE port power-cycled the device.</summary>
    PowerCycle
}

/// <summary>
/// Where a reboot reason came from. Lower values are stronger evidence, so a probe that
/// finds several can keep the best one.
/// </summary>
public enum RebootReasonSource
{
    /// <summary>
    /// The console's own per-boot reason log (<c>/var/log/reboot-time.log</c>), which records the
    /// SoC restart register. The only source that separates power loss from an internal bus hang.
    /// </summary>
    ConsoleRebootLog = 0,

    /// <summary>A kernel crash trace preserved across the reboot (<c>pstore/dmesg-ramoops-*</c>).</summary>
    PstoreCrashDump = 1,

    /// <summary>The previous boot's console ring (<c>pstore/console-ramoops-0</c>).</summary>
    PstoreConsole = 2,

    /// <summary>Inferred from device state such as a pending-upgrade marker or a firmware slot change.</summary>
    DeviceState = 3,

    /// <summary>
    /// Inferred from pstore holding no records on a device whose console ring IS configured:
    /// the RAM did not survive, so power was removed. Ranks below <see cref="DeviceState"/> so
    /// upgrade evidence (a flash can also clear the RAM) wins over the inference.
    /// </summary>
    PstoreCleared = 4,

    /// <summary>
    /// The UniFi Network event log. Generic and frequently wrong (its "unknown reason" covers
    /// power loss, hangs and panics alike), so it is only used when nothing on the device answers.
    /// </summary>
    UniFiEvent = 5
}

/// <summary>
/// Version of the evidence rules that produce a <see cref="DeviceRebootReason"/>.
///
/// Stored reasons are trusted on startup so devices are not re-probed over SSH every restart.
/// That means a correction to the rules would never reach a device whose boot was already
/// classified by the old ones. Bumping this invalidates those records: they are re-probed once
/// and rewritten. Bump it whenever a change would classify the same evidence differently.
///
/// v2: upgrade evidence outranks a bare clean shutdown, and the pending-upgrade marker is
/// matched to the boot it explains (switch upgrade reboots were reported as plain restarts).
/// v3: the marker's boot correlation actually reaches the parser - v2 shipped with the age
/// section unsplit, so the marker was still being discarded and switches kept reading "Restarted".
/// v4: firmware strings in the stored detail collapse to the version for both console and switch
/// marker shapes.
/// v5: an empty pstore on a platform that configures a console ring is read as power loss, which
/// is the only power-vs-warm-restart signal available to devices with no reset register.
/// v6: details lead with a plain-language cause and keep the technical evidence in parentheses,
/// and firmware upgrades name the version from the UniFi device data when the evidence cannot.
/// v7: a kernel crash dump only explains this boot when the console ring did NOT end deliberately
/// and the dump dates to this boot - dumps outlive the boot that produced them.
/// </summary>
/// <remarks>
/// The commanded-restart override (UniFi event overriding an unexpected pstore classification)
/// is a runtime signal, not a parser rule change - the same evidence classifies identically,
/// so no version bump is needed and stored v7 records are not re-probed.
/// </remarks>
public static class RebootClassifier
{
    /// <summary>Current rule-set version.</summary>
    public const int Version = 7;
}

/// <summary>
/// A resolved reboot reason for one device boot.
/// </summary>
/// <param name="Category">Classification used for icon and colour choices.</param>
/// <param name="Summary">Short user-facing line, e.g. "Power loss" or "Firmware upgrade".</param>
/// <param name="Detail">The evidence behind the call, shown under the summary in the tooltip.</param>
/// <param name="Source">Where the evidence came from.</param>
public record DeviceRebootReason(
    RebootCategory Category,
    string Summary,
    string? Detail,
    RebootReasonSource Source)
{
    /// <summary>True when the reason is a real finding rather than a "nothing found" placeholder.</summary>
    public bool IsConclusive => Category != RebootCategory.Unknown;

    /// <summary>
    /// True when the operator would likely want to look into it: the device did not stop on purpose.
    /// </summary>
    public bool IsUnexpected => Category is RebootCategory.PowerLoss or RebootCategory.AbruptStop
        or RebootCategory.KernelPanic or RebootCategory.HardwareHang or RebootCategory.Watchdog;

    /// <summary>Placeholder used when no source produced an answer.</summary>
    public static DeviceRebootReason Unknown(RebootReasonSource source = RebootReasonSource.UniFiEvent) =>
        new(RebootCategory.Unknown, "Reason unavailable", null, source);
}
