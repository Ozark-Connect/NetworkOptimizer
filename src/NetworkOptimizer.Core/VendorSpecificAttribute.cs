namespace NetworkOptimizer.Core;

/// <summary>
/// Marks code that contains vendor-specific assumptions (raw JSON parsing, property names, API behavior).
/// Phase 1: inventory of spots to replace with strongly-typed models and safe deserialization.
/// Phase 2: abstract behind vendor-neutral interfaces for multi-vendor support.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Parameter,
    AllowMultiple = false)]
public sealed class VendorSpecificAttribute : Attribute
{
    public string Vendor { get; }
    public string? Notes { get; }

    public VendorSpecificAttribute(string vendor, string? notes = null)
    {
        Vendor = vendor;
        Notes = notes;
    }
}
