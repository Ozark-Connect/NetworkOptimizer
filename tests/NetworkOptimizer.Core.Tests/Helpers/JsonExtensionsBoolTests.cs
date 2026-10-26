using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class JsonExtensionsBoolTests
{
    [Theory]
    [InlineData("""{"f":true}""", false, true)]
    [InlineData("""{"f":false}""", true, false)]
    [InlineData("""{"f":"true"}""", false, true)]
    [InlineData("""{"f":"True"}""", false, true)]
    [InlineData("""{"f":"false"}""", true, false)]
    [InlineData("""{"f":"yes"}""", true, true)]
    [InlineData("""{"f":1}""", false, false)]
    [InlineData("""{"f":null}""", true, true)]
    [InlineData("""{}""", true, true)]
    public void GetBoolOrDefault_ReadsBoolsAndTheirStringForm(string json, bool fallback, bool expected)
    {
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetBoolOrDefault("f", fallback).Should().Be(expected);
    }
}
