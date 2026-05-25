using System.Runtime.CompilerServices;
using NetworkOptimizer.Audit.Dns;

namespace NetworkOptimizer.Audit.Tests;

/// <summary>
/// Sets assembly-wide test defaults for static mockability hooks.
/// The [ModuleInitializer] runs once at test-assembly load before any test
/// executes, so every test in the assembly inherits these defaults without
/// per-class constructor boilerplate.
/// </summary>
/// <remarks>
/// Currently this only owns ThirdPartyDnsDetector.NextDnsProbeOverride.
/// DohProviderRegistry.DnsResolver continues to use the existing per-file
/// constructor/Dispose pattern - it's set in only two test classes and that
/// scale is fine. If future work adds more test injection points, or if the
/// DohProviderRegistry pattern picks up additional consumers, those would
/// be good candidates to migrate into this module initializer.
/// </remarks>
internal static class TestAssemblyInit
{
    [ModuleInitializer]
    internal static void Init() => SetSafeDefault();

    /// <summary>
    /// Sets ThirdPartyDnsDetector.NextDnsProbeOverride to a no-op that returns
    /// (false, null) for any input. Tests that exercise the NextDNS probe path
    /// explicitly override this with their own outcome, then call this method
    /// in Dispose to restore the assembly-wide default rather than nulling out
    /// the override (which would expose subsequent tests to the real DNS+HTTPS
    /// probe and burn their 2s DnsClient timeout).
    /// </summary>
    internal static void SetSafeDefault()
    {
        ThirdPartyDnsDetector.NextDnsProbeOverride = (_, _) =>
            Task.FromResult<(bool IsNextDns, string? Profile)>((false, null));
    }
}
