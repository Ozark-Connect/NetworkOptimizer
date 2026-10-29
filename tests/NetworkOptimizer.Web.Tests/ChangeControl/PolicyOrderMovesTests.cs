using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class PolicyOrderMovesTests
{
    [Fact]
    public void OneRuleDraggedUp_IsOneMove_WithItsNeighbors()
    {
        List<string> before = ["A", "B", "C", "D", "E", "F"];
        List<string> after = ["A", "B", "F", "C", "D", "E"];

        var move = PolicyOrderMoves.Compute(before, after).Should().ContainSingle().Subject;

        move.Name.Should().Be("F");
        move.From.Should().Be(6);
        move.To.Should().Be(3);
        move.Before.Select(n => n.Name).Should().Equal("D", "E", "F", "end of list");
        move.Before.Single(n => n.IsSubject).Position.Should().Be(6);
        move.After.Select(n => n.Name).Should().Equal("A", "B", "F", "C", "D");
        move.After.Single(n => n.IsSubject).Position.Should().Be(3);
    }

    [Fact]
    public void SwappingTwoNeighbors_IsOneMove()
    {
        PolicyOrderMoves.Compute(["A", "B", "C"], ["B", "A", "C"]).Should().ContainSingle();
    }

    [Fact]
    public void CreatedAndDeletedRules_AreNotMoves()
    {
        PolicyOrderMoves.Compute(["A", "B", "C"], ["A", "X", "C"]).Should().BeEmpty();
    }

    [Fact]
    public void DuplicateNames_MatchByPosition()
    {
        var move = PolicyOrderMoves.Compute(["Allow", "B", "Allow", "C"], ["Allow", "B", "C", "Allow"]).Should().ContainSingle().Subject;

        move.From.Should().Be(3);
        move.To.Should().Be(4);
    }

    [Fact]
    public void UnchangedOrder_HasNoMoves()
    {
        PolicyOrderMoves.Compute(["A", "B"], ["A", "B"]).Should().BeEmpty();
    }
}
