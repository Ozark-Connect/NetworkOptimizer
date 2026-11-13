using System.Globalization;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>A Float custom OID reads the same on a comma-decimal host (#1373).</summary>
public class CustomOidValueParserCommaCultureTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public CustomOidValueParserCommaCultureTests() => CultureInfo.CurrentCulture = new CultureInfo("de-DE");

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    [Theory]
    [InlineData("23.5", 23.5)]
    [InlineData("-11.25", -11.25)]
    public void Parse_Float_ReadsDotDecimalsUnderACommaCulture(string raw, double expected)
    {
        CustomOidValueParser.Parse(raw, CustomOidValueType.Float).Should().Be(expected);
    }
}
