using System.Text.Json;
using NetworkOptimizer.UniFi.Helpers;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class RadioAiSettingsTests
{
    private static JsonDocument Settings(string radioAiBody) =>
        JsonDocument.Parse($$"""
        {
            "data": [
                { "key": "global_switch", "jumboframe_enabled": true },
                { "key": "radio_ai", {{radioAiBody}} }
            ]
        }
        """);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void GetDfsEnabled_returns_configured_value_for_5ghz(bool dfs, bool expected)
    {
        var json = Settings($$"""
            "radios_configuration": [
                { "dfs": false, "channel_width": 20, "radio": "ng" },
                { "dfs": {{(dfs ? "true" : "false")}}, "channel_width": 160, "radio": "na" },
                { "dfs": true, "channel_width": 160, "radio": "6e" }
            ]
        """);

        var settings = RadioAiSettings.FromSettingsJson(json);

        Assert.NotNull(settings);
        Assert.Equal(expected, settings!.GetDfsEnabled(RadioAiSettings.Radio5GHz));
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void GetDfsEnabled_handles_flexible_bool_encodings(string dfsJson, bool expected)
    {
        var json = Settings($$"""
            "radios_configuration": [
                { "dfs": {{dfsJson}}, "channel_width": 160, "radio": "na" }
            ]
        """);

        var settings = RadioAiSettings.FromSettingsJson(json);

        Assert.Equal(expected, settings!.GetDfsEnabled(RadioAiSettings.Radio5GHz));
    }

    [Fact]
    public void GetDfsEnabled_returns_null_when_radio_absent()
    {
        var json = Settings($$"""
            "radios_configuration": [
                { "dfs": true, "channel_width": 20, "radio": "ng" }
            ]
        """);

        var settings = RadioAiSettings.FromSettingsJson(json);

        Assert.NotNull(settings);
        Assert.Null(settings!.GetDfsEnabled(RadioAiSettings.Radio5GHz));
    }

    [Fact]
    public void FromSettingsJson_returns_null_when_radio_ai_key_absent()
    {
        using var json = JsonDocument.Parse("""
        { "data": [ { "key": "global_switch", "jumboframe_enabled": true } ] }
        """);

        Assert.Null(RadioAiSettings.FromSettingsJson(json));
    }

    [Fact]
    public void FromSettingsJson_returns_null_for_null_input() =>
        Assert.Null(RadioAiSettings.FromSettingsJson(null));
}
