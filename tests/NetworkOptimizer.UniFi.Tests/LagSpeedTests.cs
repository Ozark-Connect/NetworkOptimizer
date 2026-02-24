using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class LagSpeedTests
{
    #region JSON Deserialization Tests

    [Fact]
    public void AggregatedBy_False_DeserializesToNull()
    {
        var json = """{"port_idx": 1, "speed": 1000, "aggregated_by": false}""";
        var port = JsonSerializer.Deserialize<SwitchPort>(json)!;

        port.AggregatedBy.Should().BeNull();
    }

    [Fact]
    public void AggregatedBy_Integer_DeserializesToValue()
    {
        var json = """{"port_idx": 9, "speed": 10000, "aggregated_by": 4, "lag_idx": 1}""";
        var port = JsonSerializer.Deserialize<SwitchPort>(json)!;

        port.AggregatedBy.Should().Be(4);
        port.LagIdx.Should().Be(1);
    }

    #endregion


    [Fact]
    public void NonLagPort_ReturnsIndividualSpeed()
    {
        var portTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 1000, Up = true },
            new() { PortIdx = 2, Speed = 2500, Up = true }
        };

        NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 2).Should().Be(2500);
    }

    [Fact]
    public void LagParent_ReturnsSumOfAllMemberSpeeds()
    {
        // Port 1 is LAG parent, ports 2 and 3 are children (2x10G = 20G)
        var portTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 10000, Up = true },
            new() { PortIdx = 2, Speed = 10000, Up = true, AggregatedBy = 1, LagIdx = 1 },
            new() { PortIdx = 3, Speed = 10000, Up = true, AggregatedBy = 1, LagIdx = 1 },
            new() { PortIdx = 4, Speed = 1000, Up = true }
        };

        NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 1).Should().Be(30000);
    }

    [Fact]
    public void LagChild_ReturnsSameAggregateAsParent()
    {
        var portTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 10000, Up = true },
            new() { PortIdx = 2, Speed = 10000, Up = true, AggregatedBy = 1, LagIdx = 1 },
            new() { PortIdx = 3, Speed = 10000, Up = true, AggregatedBy = 1, LagIdx = 1 }
        };

        // Querying a child should return the same aggregate as querying the parent
        var parentSpeed = NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 1);
        var childSpeed = NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 2);

        parentSpeed.Should().Be(30000);
        childSpeed.Should().Be(parentSpeed);
    }

    [Fact]
    public void LagWithDownChild_ExcludesDownPortFromAggregate()
    {
        var portTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 10000, Up = true },
            new() { PortIdx = 2, Speed = 10000, Up = true, AggregatedBy = 1, LagIdx = 1 },
            new() { PortIdx = 3, Speed = 10000, Up = false, AggregatedBy = 1, LagIdx = 1 }
        };

        // Only parent + one up child = 20G
        NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 1).Should().Be(20000);
    }

    [Fact]
    public void PortNotFound_ReturnsZero()
    {
        var portTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 1000, Up = true }
        };

        NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 99).Should().Be(0);
    }

    [Fact]
    public void LagWithDownParent_ExcludesParentSpeed()
    {
        var portTable = new List<SwitchPort>
        {
            new() { PortIdx = 1, Speed = 10000, Up = false },
            new() { PortIdx = 2, Speed = 10000, Up = true, AggregatedBy = 1, LagIdx = 1 },
            new() { PortIdx = 3, Speed = 10000, Up = true, AggregatedBy = 1, LagIdx = 1 }
        };

        // Parent is down, only children count
        NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 1).Should().Be(20000);
    }

    [Fact]
    public void TwoChildLag_ReturnsCorrectAggregate()
    {
        // Real-world scenario: 2x2.5G LAG
        var portTable = new List<SwitchPort>
        {
            new() { PortIdx = 5, Speed = 2500, Up = true },
            new() { PortIdx = 6, Speed = 2500, Up = true, AggregatedBy = 5, LagIdx = 2 }
        };

        NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 5).Should().Be(5000);
        NetworkPathAnalyzer.GetLagAggregateSpeed(portTable, 6).Should().Be(5000);
    }
}
