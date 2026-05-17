using System.Text.Json;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class FlexibleIntConverterTests
{
    private record IntHolder([property: System.Text.Json.Serialization.JsonConverter(typeof(FlexibleIntConverter))] int? Value);

    [Theory]
    [InlineData("4", 4)]
    [InlineData("0", 0)]
    [InlineData("-1", -1)]
    [InlineData("2147483647", int.MaxValue)]
    public void Reads_integer(string json, int expected)
    {
        var result = JsonSerializer.Deserialize<IntHolder>($"{{\"Value\":{json}}}");
        Assert.Equal(expected, result!.Value);
    }

    [Theory]
    [InlineData("4.0", 4)]
    [InlineData("1.9", 1)]
    [InlineData("0.0", 0)]
    public void Reads_float_as_truncated_int(string json, int expected)
    {
        var result = JsonSerializer.Deserialize<IntHolder>($"{{\"Value\":{json}}}");
        Assert.Equal(expected, result!.Value);
    }

    [Theory]
    [InlineData("\"4\"", 4)]
    [InlineData("\"0\"", 0)]
    [InlineData("\"-7\"", -7)]
    public void Reads_string_integer(string json, int expected)
    {
        var result = JsonSerializer.Deserialize<IntHolder>($"{{\"Value\":{json}}}");
        Assert.Equal(expected, result!.Value);
    }

    [Fact]
    public void Reads_null_as_null()
    {
        var result = JsonSerializer.Deserialize<IntHolder>("{\"Value\":null}");
        Assert.Null(result!.Value);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"abc\"")]
    public void Reads_non_numeric_string_as_null(string json)
    {
        var result = JsonSerializer.Deserialize<IntHolder>($"{{\"Value\":{json}}}");
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Missing_property_is_null()
    {
        var result = JsonSerializer.Deserialize<IntHolder>("{}");
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Writes_value()
    {
        var json = JsonSerializer.Serialize(new IntHolder(42));
        Assert.Contains("42", json);
    }

    [Fact]
    public void Writes_null()
    {
        var json = JsonSerializer.Serialize(new IntHolder(null));
        Assert.Contains("null", json);
    }
}

public class FlexibleBoolConverterTests
{
    private record BoolHolder([property: System.Text.Json.Serialization.JsonConverter(typeof(FlexibleBoolConverter))] bool Value);

    [Fact]
    public void Reads_true() =>
        Assert.True(JsonSerializer.Deserialize<BoolHolder>("{\"Value\":true}")!.Value);

    [Fact]
    public void Reads_false() =>
        Assert.False(JsonSerializer.Deserialize<BoolHolder>("{\"Value\":false}")!.Value);

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"True\"", true)]
    [InlineData("\"False\"", false)]
    public void Reads_string_bool(string json, bool expected)
    {
        var result = JsonSerializer.Deserialize<BoolHolder>($"{{\"Value\":{json}}}");
        Assert.Equal(expected, result!.Value);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("42", true)]
    public void Reads_number_as_bool(string json, bool expected)
    {
        var result = JsonSerializer.Deserialize<BoolHolder>($"{{\"Value\":{json}}}");
        Assert.Equal(expected, result!.Value);
    }

    [Fact]
    public void Reads_non_boolean_string_as_false()
    {
        var result = JsonSerializer.Deserialize<BoolHolder>("{\"Value\":\"garbage\"}");
        Assert.False(result!.Value);
    }

    [Fact]
    public void Writes_true()
    {
        var json = JsonSerializer.Serialize(new BoolHolder(true));
        Assert.Contains("true", json);
    }

    [Fact]
    public void Writes_false()
    {
        var json = JsonSerializer.Serialize(new BoolHolder(false));
        Assert.Contains("false", json);
    }
}

public class FlexibleNullableBoolConverterTests
{
    private record NullBoolHolder([property: System.Text.Json.Serialization.JsonConverter(typeof(FlexibleNullableBoolConverter))] bool? Value);

    [Fact]
    public void Reads_true() =>
        Assert.True(JsonSerializer.Deserialize<NullBoolHolder>("{\"Value\":true}")!.Value);

    [Fact]
    public void Reads_false() =>
        Assert.False(JsonSerializer.Deserialize<NullBoolHolder>("{\"Value\":false}")!.Value);

    [Fact]
    public void Reads_null_as_null()
    {
        var result = JsonSerializer.Deserialize<NullBoolHolder>("{\"Value\":null}");
        Assert.Null(result!.Value);
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    public void Reads_string_bool(string json, bool expected)
    {
        var result = JsonSerializer.Deserialize<NullBoolHolder>($"{{\"Value\":{json}}}");
        Assert.Equal(expected, result!.Value);
    }

    [Fact]
    public void Reads_non_boolean_string_as_null()
    {
        var result = JsonSerializer.Deserialize<NullBoolHolder>("{\"Value\":\"garbage\"}");
        Assert.Null(result!.Value);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Reads_number_as_bool(string json, bool expected)
    {
        var result = JsonSerializer.Deserialize<NullBoolHolder>($"{{\"Value\":{json}}}");
        Assert.Equal(expected, result!.Value);
    }

    [Fact]
    public void Missing_property_is_null()
    {
        var result = JsonSerializer.Deserialize<NullBoolHolder>("{}");
        Assert.Null(result!.Value);
    }
}
