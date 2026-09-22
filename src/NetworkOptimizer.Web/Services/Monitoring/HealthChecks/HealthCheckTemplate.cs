using System.Text.Json.Serialization;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>
/// A shipped health check the user can start from: every definition field pre-filled, plus the
/// plain-language lines the editor shows. One JSON file per template under
/// <c>wwwroot/data/health-checks/</c>.
/// </summary>
public class HealthCheckTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Device types the template fits: gateway, switch, ap.</summary>
    public List<string> AppliesTo { get; set; } = new();

    public string? WhatItWatches { get; set; }
    public string? WhenItFires { get; set; }
    public string? WhatItDoes { get; set; }

    public string FieldName { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 20;
    public int IntervalSeconds { get; set; } = 60;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HealthCheckParser Parser { get; set; } = HealthCheckParser.FirstNumber;
    public string? ParserArg { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HealthCheckOperator Operator { get; set; } = HealthCheckOperator.GreaterOrEqual;
    public double Threshold { get; set; }
    public int ConsecutiveSamples { get; set; } = 2;
    public string? NotApplicablePattern { get; set; }

    /// <summary>Unit shown after the value on the chart and in the table, e.g. "%".</summary>
    public string? Unit { get; set; }

    public bool AlertEnabled { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlertSeverity AlertSeverity { get; set; } = AlertSeverity.Warning;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HealthCheckRemedy Remedy { get; set; } = HealthCheckRemedy.None;
    public string? RemedyArg { get; set; }

    /// <summary>Remedies the editor offers for this template. Empty means all of them.</summary>
    [JsonConverter(typeof(JsonStringEnumListConverter))]
    public List<HealthCheckRemedy> AllowedRemedies { get; set; } = new();

    public int RemedyCooldownSeconds { get; set; } = 1800;
    public int RemedyMaxPerDay { get; set; } = 4;

    /// <summary>Whether the template fits a device type.</summary>
    public bool Fits(DeviceType type)
    {
        if (AppliesTo.Count == 0) return true;
        var key = type switch
        {
            DeviceType.Gateway => "gateway",
            DeviceType.Switch => "switch",
            DeviceType.AccessPoint => "ap",
            _ => "",
        };
        return AppliesTo.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the editor may offer a remedy for a check made from this template.</summary>
    public bool Allows(HealthCheckRemedy remedy) =>
        AllowedRemedies.Count == 0 || AllowedRemedies.Contains(remedy);

    /// <summary>A fresh definition pre-filled from the template.</summary>
    public HealthCheckDefinition ToDefinition(string deviceMac) => new()
    {
        DeviceMac = deviceMac,
        Name = Name,
        FieldName = FieldName,
        TemplateId = Id,
        Enabled = true,
        IntervalSeconds = IntervalSeconds,
        Command = Command,
        TimeoutSeconds = TimeoutSeconds,
        Parser = Parser,
        ParserArg = ParserArg,
        Operator = Operator,
        Threshold = Threshold,
        ConsecutiveSamples = ConsecutiveSamples,
        NotApplicablePattern = NotApplicablePattern,
        AlertEnabled = AlertEnabled,
        AlertSeverity = (int)AlertSeverity,
        Remedy = Remedy,
        RemedyArg = RemedyArg,
        RemedyCooldownSeconds = RemedyCooldownSeconds,
        RemedyMaxPerDay = RemedyMaxPerDay,
        Description = Description,
    };
}

/// <summary>Reads a JSON array of enum names into a list of <see cref="HealthCheckRemedy"/>.</summary>
public sealed class JsonStringEnumListConverter : JsonConverter<List<HealthCheckRemedy>>
{
    public override List<HealthCheckRemedy> Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var list = new List<HealthCheckRemedy>();
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray) return list;
        while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.String
                && Enum.TryParse<HealthCheckRemedy>(reader.GetString(), ignoreCase: true, out var value))
                list.Add(value);
        }
        return list;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, List<HealthCheckRemedy> value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var v in value) writer.WriteStringValue(v.ToString());
        writer.WriteEndArray();
    }
}
