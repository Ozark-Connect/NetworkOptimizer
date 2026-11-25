using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The console's responses are read from a stream (ReadFromJsonAsync), so a converter sees a partial
/// buffer, where Utf8JsonReader.Skip() throws instead of skipping. Deserializing from a string never
/// shows that, which is how an unexpected shape on one device emptied a whole device list. Every case
/// here reads through a small-buffered stream with the value it has to skip placed past the first buffer.
/// </summary>
public class FlexibleConverterStreamingTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultBufferSize = 16 };

    private sealed class Holder
    {
        [JsonPropertyName("pad")] public string? Pad { get; set; }

        [JsonPropertyName("list")]
        [JsonConverter(typeof(FlexibleStringListConverter))]
        public List<string>? List { get; set; }

        [JsonPropertyName("scalar")]
        [JsonConverter(typeof(FlexibleScalarStringConverter))]
        public string? Scalar { get; set; }

        [JsonPropertyName("when")]
        [JsonConverter(typeof(FlexibleDateTimeOffsetConverter))]
        public DateTimeOffset? When { get; set; }

        [JsonPropertyName("after")] public string? After { get; set; }
    }

    private static async Task<Holder?> ReadStreamed(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await JsonSerializer.DeserializeAsync<Holder>(stream, Options);
    }

    private static readonly string Pad = new('x', 200);

    // Data after the skipped value keeps the reader's block from being final, as in a real response.
    private static readonly string Tail = new('y', 64 * 1024);

    [Fact]
    public async Task StringList_ObjectValue_ReadsAsNull()
    {
        var h = await ReadStreamed($$$"""{"pad":"{{{Pad}}}","list":{"a":{"b":[1,2,3]}},"after":"ok","tail":"{{{Tail}}}"}""");
        h!.List.Should().BeNull();
        h.After.Should().Be("ok");
    }

    [Fact]
    public async Task StringList_NestedNonStringItems_AreSkipped()
    {
        var h = await ReadStreamed($$$"""{"pad":"{{{Pad}}}","list":["a",{"x":[1,{"y":2}]},"b"],"after":"ok","tail":"{{{Tail}}}"}""");
        h!.List.Should().Equal("a", "b");
        h.After.Should().Be("ok");
    }

    [Fact]
    public async Task ScalarString_ObjectValue_ReadsAsNull()
    {
        var h = await ReadStreamed($$$"""{"pad":"{{{Pad}}}","scalar":{"change_count":35,"mst":0},"after":"ok","tail":"{{{Tail}}}"}""");
        h!.Scalar.Should().BeNull();
        h.After.Should().Be("ok");
    }

    [Fact]
    public async Task DateTimeOffset_ArrayValue_ReadsAsNull()
    {
        var h = await ReadStreamed($$$"""{"pad":"{{{Pad}}}","when":[{"a":1},{"b":2}],"after":"ok","tail":"{{{Tail}}}"}""");
        h!.When.Should().BeNull();
        h.After.Should().Be("ok");
    }
}
