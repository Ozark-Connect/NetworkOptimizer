using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>
/// Traffic-flows paging ends on a short page. The console reports has_next false on every page past
/// the first, so paging on it stopped after 1,000 records.
/// </summary>
public class FlowPagingTests
{
    private static JsonElement Page(int records, bool hasNext) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            data = Enumerable.Range(0, records).Select(i => new { id = i.ToString() }),
            has_next = hasNext,
        })).RootElement.Clone();

    [Fact]
    public void FullPageIsNotTheLastEvenWhenHasNextIsFalse() =>
        ThreatCollectionService.IsLastFlowPage(Page(ThreatCollectionService.FlowPageSize, hasNext: false)).Should().BeFalse();

    [Fact]
    public void ShortPageIsTheLast() =>
        ThreatCollectionService.IsLastFlowPage(Page(ThreatCollectionService.FlowPageSize - 1, hasNext: true)).Should().BeTrue();

    [Fact]
    public void MissingDataIsTheLast() =>
        ThreatCollectionService.IsLastFlowPage(JsonDocument.Parse("{}").RootElement.Clone()).Should().BeTrue();
}
