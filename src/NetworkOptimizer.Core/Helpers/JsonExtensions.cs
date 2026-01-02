using System.Text.Json;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Extension methods for System.Text.Json to simplify property extraction.
/// Reduces boilerplate when parsing UniFi API responses.
/// </summary>
public static class JsonExtensions
{
    /// <summary>
    /// Get a string property value, or null if not found.
    /// </summary>
    public static string? GetStringOrNull(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    /// <summary>
    /// Get a string property value, or a default if not found.
    /// </summary>
    public static string GetStringOrDefault(this JsonElement element, string propertyName, string defaultValue = "")
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? defaultValue
            : defaultValue;
    }

    /// <summary>
    /// Get a string property, trying multiple property names in order.
    /// Returns the first non-null value found, or null if none found.
    /// </summary>
    public static string? GetStringFromAny(this JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }
        return null;
    }

    /// <summary>
    /// Get an integer property value, or a default if not found.
    /// </summary>
    public static int GetIntOrDefault(this JsonElement element, string propertyName, int defaultValue = 0)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : defaultValue;
    }

    /// <summary>
    /// Get a long property value, or null if not found.
    /// </summary>
    public static long? GetLongOrNull(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64()
            : null;
    }

    /// <summary>
    /// Get a double property value, or a default if not found.
    /// Handles both numeric values and string representations.
    /// </summary>
    public static double GetDoubleOrDefault(this JsonElement element, string propertyName, double defaultValue = 0.0)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return defaultValue;

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetDouble(),
            JsonValueKind.String when double.TryParse(prop.GetString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    /// <summary>
    /// Get a boolean property value, or a default if not found.
    /// </summary>
    public static bool GetBoolOrDefault(this JsonElement element, string propertyName, bool defaultValue = false)
    {
        return element.TryGetProperty(propertyName, out var prop) &&
               (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
            ? prop.GetBoolean()
            : defaultValue;
    }

    /// <summary>
    /// Get a string array property value, or null if not found.
    /// Filters out null and empty strings.
    /// </summary>
    public static List<string>? GetStringArrayOrNull(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return null;

        var result = prop.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Try to get an array property and enumerate it.
    /// Returns an empty enumerable if property doesn't exist or isn't an array.
    /// </summary>
    public static IEnumerable<JsonElement> GetArrayOrEmpty(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array
            ? prop.EnumerateArray()
            : Enumerable.Empty<JsonElement>();
    }

    /// <summary>
    /// Unwrap a response that has a "data" property containing an array.
    /// Common pattern in UniFi API responses.
    /// </summary>
    public static IEnumerable<JsonElement> UnwrapDataArray(this JsonElement element)
    {
        // Handle array at root
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray();

        // Handle object with "data" property
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("data", out var dataArray) &&
            dataArray.ValueKind == JsonValueKind.Array)
        {
            return dataArray.EnumerateArray();
        }

        // Handle single object - return as single-item enumerable
        if (element.ValueKind == JsonValueKind.Object)
            return new[] { element };

        return Enumerable.Empty<JsonElement>();
    }
}
