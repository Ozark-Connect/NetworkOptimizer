using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.CableModemProviders.Uci;

/// <summary>
/// The decrypted inform JSON from a UniFi Cable Internet. Only the fields this feature reads are
/// modeled. Field names follow the open-source uci-inform-exporter, which reads the same payload.
/// Values arrive as numbers or as strings, so every numeric field is read tolerantly.
/// </summary>
[VendorSpecific("UniFi", "UCI inform payload: DOCSIS channel tables, event log, CI state")]
public sealed class UciInformPayload
{
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    [JsonPropertyName("uptime")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Uptime { get; set; }

    /// <summary>Downstream SC-QAM channels.</summary>
    [JsonPropertyName("ds_table")]
    public List<UciChannelRow>? DsTable { get; set; }

    /// <summary>Downstream OFDM (DOCSIS 3.1) channels.</summary>
    [JsonPropertyName("ofdm_table")]
    public List<UciChannelRow>? OfdmTable { get; set; }

    /// <summary>Upstream SC-QAM channels.</summary>
    [JsonPropertyName("us_table")]
    public List<UciChannelRow>? UsTable { get; set; }

    /// <summary>Upstream OFDMA (DOCSIS 3.1) channels.</summary>
    [JsonPropertyName("ofdma_table")]
    public List<UciChannelRow>? OfdmaTable { get; set; }

    /// <summary>The modem's DOCSIS event log: newline-separated lines of text.</summary>
    [JsonPropertyName("ev_log_docsis")]
    public string? EventLog { get; set; }

    [JsonPropertyName("ci_state_table")]
    public UciStateTable? StateTable { get; set; }

    private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    /// <summary>Parses decrypted inform JSON; null when it is not a JSON object.</summary>
    public static UciInformPayload? Parse(byte[] json)
    {
        try { return JsonSerializer.Deserialize<UciInformPayload>(json, Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>One row of a UCI channel table (SC-QAM or OFDM/OFDMA, downstream or upstream).</summary>
[VendorSpecific("UniFi", "UCI inform channel table row")]
public sealed class UciChannelRow
{
    [JsonPropertyName("ch_id")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? ChannelId { get; set; }

    /// <summary>Center frequency. Seen as MHz; <see cref="UciStatsMapper"/> also accepts Hz.</summary>
    [JsonPropertyName("freq")]
    [JsonConverter(typeof(UciFlexibleStringConverter))]
    public string? Frequency { get; set; }

    [JsonPropertyName("modulation")]
    [JsonConverter(typeof(UciFlexibleStringConverter))]
    public string? Modulation { get; set; }

    /// <summary>Lock state, "Locked" when locked.</summary>
    [JsonPropertyName("state")]
    [JsonConverter(typeof(UciFlexibleStringConverter))]
    public string? State { get; set; }

    /// <summary>Power in dBmV (receive downstream, transmit upstream).</summary>
    [JsonPropertyName("pwr")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double? Power { get; set; }

    /// <summary>SNR/MER in dB. SC-QAM rows report it negative; the magnitude is the value.</summary>
    [JsonPropertyName("snr")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double? Snr { get; set; }

    /// <summary>Reported channel capacity in Mbps.</summary>
    [JsonPropertyName("speed")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double? Speed { get; set; }

    [JsonPropertyName("correctable")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Correctable { get; set; }

    [JsonPropertyName("uncorrectable")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Uncorrectable { get; set; }
}

/// <summary>The UCI's DOCSIS registration state.</summary>
[VendorSpecific("UniFi", "UCI inform ci_state_table")]
public sealed class UciStateTable
{
    [JsonPropertyName("ci_state")]
    public string? State { get; set; }

    [JsonPropertyName("ci_reinit_reason")]
    public string? ReinitReason { get; set; }

    [JsonPropertyName("ci_mode")]
    public string? Mode { get; set; }
}

/// <summary>Reads a string, a number, or a bool as its text; null for anything else.</summary>
public sealed class UciFlexibleStringConverter : JsonConverter<string?>
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
