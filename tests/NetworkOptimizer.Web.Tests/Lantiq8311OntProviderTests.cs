using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class Lantiq8311OntProviderTests
{
    // Verbatim output of `pontop -g "FEC Status & Counters" -b` from a real 8311/WAS-110 (healthy link).
    private const string HealthyFecPage =
        "Page: FEC Status & Counters\n" +
        "OPTION                                              VALUE\n" +
        "FEC upstream                                       : ON\n" +
        "FEC downstream                                     : ON\n" +
        " \n" +
        "BIP errors                                         : 0\n" +
        "Total FEC codewords                                : 289895929785\n" +
        "Corrected FEC codewords                            : 0\n" +
        "Uncorrected FEC codewords                          : 0\n" +
        "Corrected FEC bytes                                : 0\n" +
        "FEC errored seconds                                : 0\n";

    [Fact]
    public void ParseFecCounters_HealthyPage_ReadsZeroFecAndBip()
    {
        var stats = new OntStats();
        Lantiq8311OntProvider.ParseFecCounters(HealthyFecPage, stats);

        stats.FecErrors.Should().Be(0);
        stats.BipErrors.Should().Be(0);
    }

    [Fact]
    public void ParseFecCounters_DegradedPage_MapsUncorrectedAndBip_IgnoresCorrected()
    {
        var page =
            "Page: FEC Status & Counters\n" +
            "BIP errors                                         : 42\n" +
            "Total FEC codewords                                : 289895929785\n" +
            "Corrected FEC codewords                            : 150000\n" +
            "Uncorrected FEC codewords                          : 1234\n" +
            "FEC errored seconds                                : 7\n";

        var stats = new OntStats();
        Lantiq8311OntProvider.ParseFecCounters(page, stats);

        // FecErrors is the data-loss signal (uncorrectable), NOT the benign corrected count.
        stats.FecErrors.Should().Be(1234);
        stats.BipErrors.Should().Be(42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\nno colons here\nOPTION  VALUE")]
    [InlineData("FEC upstream                                       : ON")]
    public void ParseFecCounters_NoNumericRows_LeavesCountersNull(string text)
    {
        var stats = new OntStats();
        Lantiq8311OntProvider.ParseFecCounters(text, stats);

        stats.FecErrors.Should().BeNull();
        stats.BipErrors.Should().BeNull();
    }
}
