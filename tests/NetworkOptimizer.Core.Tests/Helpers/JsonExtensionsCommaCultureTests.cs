using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

/// <summary>A numeric string in device JSON reads the same on a comma-decimal host (#1373).</summary>
public class JsonExtensionsCommaCultureTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public JsonExtensionsCommaCultureTests() => CultureInfo.CurrentCulture = new CultureInfo("de-DE");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    [Theory]
    [InlineData("""{"v":"-92.5"}""", -92.5)]
    [InlineData("""{"v":"0.85"}""", 0.85)]
    [InlineData("""{"v":-92.5}""", -92.5)]
    public void GetDoubleOrDefault_ReadsDotDecimalsUnderACommaCulture(string json, double expected)
    {
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetDoubleOrDefault("v").Should().Be(expected);
    }
}
