using System;
using FluentAssertions;

[assembly: FluentAssertions.Extensibility.AssertionEngineInitializer(
    typeof(FluentAssertionsLicenseInitializer),
    nameof(FluentAssertionsLicenseInitializer.Initialize))]

/// <summary>
/// Initializes FluentAssertions license acknowledgment.
/// Commercial license held by Ozark Connect (Invoice #38609).
/// </summary>
public static class FluentAssertionsLicenseInitializer
{
    public static void Initialize()
    {
        // Presence of env var indicates valid commercial license
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLUENT_ASSERTIONS_LICENSED")))
        {
            License.Accepted = true;
        }
    }
}
