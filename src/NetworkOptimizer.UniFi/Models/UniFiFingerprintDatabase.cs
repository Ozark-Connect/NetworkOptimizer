using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Converter that handles both string and number JSON values, converting to string
/// </summary>
public class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetInt64().ToString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}

/// <summary>
/// Response from /proxy/network/v2/api/fingerprint_devices/{index}
/// Contains the UniFi device fingerprint database mappings
/// </summary>
public class UniFiFingerprintDatabase
{
    /// <summary>
    /// Mapping of device type IDs to human-readable names
    /// e.g., "9" -> "IP Network Camera", "42" -> "Smart Plug"
    /// </summary>
    [JsonPropertyName("dev_type_ids")]
    public Dictionary<string, string> DevTypeIds { get; set; } = new();

    /// <summary>
    /// Mapping of family IDs to human-readable names
    /// e.g., "5" -> "Intelligent Home Appliances", "7" -> "Network & Peripheral"
    /// </summary>
    [JsonPropertyName("family_ids")]
    public Dictionary<string, string> FamilyIds { get; set; } = new();

    /// <summary>
    /// Mapping of vendor IDs to vendor names
    /// e.g., "244" -> "Amazon", "232" -> "Ring"
    /// </summary>
    [JsonPropertyName("vendor_ids")]
    public Dictionary<string, string> VendorIds { get; set; } = new();

    /// <summary>
    /// Mapping of OS class IDs to OS names
    /// e.g., "5" -> "Android", "15" -> "Apple iOS"
    /// </summary>
    [JsonPropertyName("os_class_ids")]
    public Dictionary<string, string> OsClassIds { get; set; } = new();

    /// <summary>
    /// Mapping of OS name IDs to specific OS versions
    /// </summary>
    [JsonPropertyName("os_name_ids")]
    public Dictionary<string, string> OsNameIds { get; set; } = new();

    /// <summary>
    /// Mapping of device fingerprint IDs to specific device entries
    /// </summary>
    [JsonPropertyName("dev_ids")]
    public Dictionary<string, FingerprintDeviceEntry> DevIds { get; set; } = new();

    /// <summary>
    /// Get device type name by ID
    /// </summary>
    public string? GetDeviceTypeName(int? devTypeId) =>
        devTypeId.HasValue && DevTypeIds.TryGetValue(devTypeId.Value.ToString(), out var name) ? name : null;

    /// <summary>
    /// Get family name by ID
    /// </summary>
    public string? GetFamilyName(int? familyId) =>
        familyId.HasValue && FamilyIds.TryGetValue(familyId.Value.ToString(), out var name) ? name : null;

    /// <summary>
    /// Get vendor name by ID
    /// </summary>
    public string? GetVendorName(int? vendorId) =>
        vendorId.HasValue && VendorIds.TryGetValue(vendorId.Value.ToString(), out var name) ? name : null;

    /// <summary>
    /// Merge another fingerprint database into this one
    /// </summary>
    public void Merge(UniFiFingerprintDatabase other)
    {
        foreach (var kvp in other.DevTypeIds)
            DevTypeIds.TryAdd(kvp.Key, kvp.Value);
        foreach (var kvp in other.FamilyIds)
            FamilyIds.TryAdd(kvp.Key, kvp.Value);
        foreach (var kvp in other.VendorIds)
            VendorIds.TryAdd(kvp.Key, kvp.Value);
        foreach (var kvp in other.OsClassIds)
            OsClassIds.TryAdd(kvp.Key, kvp.Value);
        foreach (var kvp in other.OsNameIds)
            OsNameIds.TryAdd(kvp.Key, kvp.Value);
        foreach (var kvp in other.DevIds)
            DevIds.TryAdd(kvp.Key, kvp.Value);
    }
}

/// <summary>
/// Individual device entry in the fingerprint database
/// </summary>
public class FingerprintDeviceEntry
{
    /// <summary>
    /// Device type category ID (maps to dev_type_ids)
    /// </summary>
    [JsonPropertyName("dev_type_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? DevTypeId { get; set; }

    /// <summary>
    /// Device family ID (maps to family_ids)
    /// </summary>
    [JsonPropertyName("family_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? FamilyId { get; set; }

    /// <summary>
    /// Vendor ID (maps to vendor_ids)
    /// </summary>
    [JsonPropertyName("vendor_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? VendorId { get; set; }

    /// <summary>
    /// Specific device/model name
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// OS class ID
    /// </summary>
    [JsonPropertyName("os_class_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? OsClassId { get; set; }

    /// <summary>
    /// OS name ID
    /// </summary>
    [JsonPropertyName("os_name_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? OsNameId { get; set; }

    /// <summary>
    /// Facebook device ID (for social device detection)
    /// </summary>
    [JsonPropertyName("fb_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? FbId { get; set; }

    /// <summary>
    /// TradeMark ID
    /// </summary>
    [JsonPropertyName("tm_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? TmId { get; set; }

    /// <summary>
    /// Category tag ID
    /// </summary>
    [JsonPropertyName("ctag_id")]
    [JsonConverter(typeof(StringOrNumberConverter))]
    public string? CtagId { get; set; }
}
