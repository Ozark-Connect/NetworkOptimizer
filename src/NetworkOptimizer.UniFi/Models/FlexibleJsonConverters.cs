using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

// Tolerant System.Text.Json converters for vendor fields that arrive in more than one shape.
// Name new ones Flexible* and add them here.

/// <summary>
/// JSON converter that handles bool values that may come as strings ("true"/"false") instead of native booleans.
/// UniFi OS Server returns some boolean fields as strings.
/// </summary>
public class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => bool.TryParse(reader.GetString(), out var value) && value,
            JsonTokenType.Number => reader.GetInt32() != 0,
            _ => false
        };
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}

/// <summary>
/// Nullable version of FlexibleBoolConverter for fields that may be absent from the response.
/// </summary>
public class FlexibleNullableBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => bool.TryParse(reader.GetString(), out var value) ? value : null,
            JsonTokenType.Number => reader.GetInt32() != 0,
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteBooleanValue(value.Value);
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// JSON converter that handles int values that may come as strings, empty strings, or null.
/// UniFi API sometimes returns VLAN IDs as strings or empty strings instead of numbers.
/// </summary>
public class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var i) => i,
            JsonTokenType.Number => (int)reader.GetDouble(),
            JsonTokenType.String when int.TryParse(reader.GetString(), out var value) => value,
            JsonTokenType.String => null, // Empty string or non-numeric string
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// <see cref="FlexibleIntConverter"/> for a non-nullable int: a number, a stringified number, a
/// float, or nothing. A value that cannot be read is 0, which port and speed readers already
/// treat as "none".
/// </summary>
public class FlexibleNonNullableIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var i) => i,
            JsonTokenType.Number => (int)reader.GetDouble(),
            JsonTokenType.String when int.TryParse(reader.GetString(), out var value) => value,
            _ => 0,
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// Tolerant parser for 64-bit counters (byte and packet totals) that may arrive as a number, a
/// stringified number, a float, or nothing. Mirrors FlexibleIntConverter; a value that cannot be
/// read is 0, which the byte-delta readers treat as "no sample" rather than as a reset.
/// </summary>
public class FlexibleLongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out var l) => l,
            JsonTokenType.Number => (long)reader.GetDouble(),
            JsonTokenType.String when long.TryParse(reader.GetString(), out var value) => value,
            _ => 0,
        };
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// Tolerant nullable-double parser for UniFi API fields that may arrive as a number, a
/// stringified number, or an empty string. Mirrors FlexibleIntConverter but for floating
/// point. SFP DDM values (rxpower, current, temperature, voltage) are the primary case;
/// some firmwares stringify them.
/// </summary>
public class FlexibleDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String when double.TryParse(
                reader.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) => value,
            JsonTokenType.String => null, // Empty / non-numeric string
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// Tolerant string-list parser: an array of strings, a single string (as a one-item list), or null.
/// Anything else reads as null instead of failing the whole object it sits in.
/// </summary>
public class FlexibleStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return [reader.GetString()!];
            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String) list.Add(reader.GetString()!);
                    else reader.Skip();
                }
                return list;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Reads a string or an integer as a string. Any other token throws, failing the whole object.
/// </summary>
public class FlexibleStringOrNumberConverter : JsonConverter<string?>
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

/// <summary>Reads a string, a number, or a bool as its text; null for anything else.</summary>
public sealed class FlexibleScalarStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => SkipAndNull(ref reader),
        };

    private static string? SkipAndNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>
/// Tolerant nullable ISO 8601 timestamp parser. Null, an empty or unparsable string, a non-string,
/// or a zero date (before 2000) reads as null instead of failing the whole object it sits in.
/// </summary>
public class FlexibleDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly DateTimeOffset Earliest = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        return DateTimeOffset.TryParse(
                   reader.GetString(),
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal,
                   out var value)
               && value >= Earliest
            ? value
            : null;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value);
        else
            writer.WriteNullValue();
    }
}
