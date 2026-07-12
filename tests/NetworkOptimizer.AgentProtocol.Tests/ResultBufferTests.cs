using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class ResultBufferTests
{
    private static AgentMessage ProbeMessage(string targetId, long timestampMs = 1000) => new()
    {
        ProbeResults = new ProbeResultBatch
        {
            Results =
            {
                new ProbeResult
                {
                    TargetId = targetId,
                    TimestampUnixMs = timestampMs,
                    Success = true,
                    Sent = 5,
                    Received = 5,
                }
            }
        }
    };

    private static AgentMessage SnmpMessage(string deviceMac) => new()
    {
        SnmpResults = new SnmpResultBatch
        {
            Interfaces =
            {
                new SnmpInterfaceSample
                {
                    DeviceMac = deviceMac,
                    IfName = "eth0",
                    InOctets = 123456,
                    OutOctets = 654321,
                    TimestampUnixMs = 1000,
                }
            }
        }
    };

    // Peek the oldest single frame (maxSamples: 1 disables coalescing) and ack it,
    // mirroring a consumer that confirms each message - the ack-era stand-in for
    // the old remove-on-dequeue.
    private static async Task<AgentMessage> NextAsync(ResultBuffer buffer, CancellationToken ct = default)
    {
        var frame = await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, ct);
        buffer.MarkAcked(frame.ThroughSeq);
        return frame.Message;
    }

    [Fact]
    public async Task ReplaysInFifoOrder()
    {
        var buffer = new ResultBuffer();
        buffer.Enqueue(ProbeMessage("a"));
        buffer.Enqueue(SnmpMessage("aa:bb:cc:dd:ee:ff"));
        buffer.Enqueue(ProbeMessage("b"));

        (await NextAsync(buffer)).ProbeResults.Results[0].TargetId.Should().Be("a");
        (await NextAsync(buffer)).PayloadCase.Should().Be(AgentMessage.PayloadOneofCase.SnmpResults);
        (await NextAsync(buffer)).ProbeResults.Results[0].TargetId.Should().Be("b");
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public async Task TakeWaitsForEnqueue()
    {
        var buffer = new ResultBuffer();
        var take = buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, CancellationToken.None).AsTask();
        take.IsCompleted.Should().BeFalse();

        buffer.Enqueue(ProbeMessage("late"));
        var frame = await take.WaitAsync(TimeSpan.FromSeconds(5));
        frame.Message.ProbeResults.Results[0].TargetId.Should().Be("late");
    }

    [Fact]
    public async Task TakeHonorsCancellation()
    {
        var buffer = new ResultBuffer();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = async () => await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task UnackedFramesReplayUntilAcked()
    {
        var buffer = new ResultBuffer();
        buffer.Enqueue(ProbeMessage("a"));
        buffer.Enqueue(ProbeMessage("b"));
        buffer.Enqueue(ProbeMessage("c"));

        // Drain all three without acking - as if the writes went into a black hole.
        long cursor = 0;
        for (var i = 0; i < 3; i++)
        {
            var f = await buffer.TakeUnsentBatchAsync(cursor, maxSamples: 1, CancellationToken.None);
            cursor = f.ThroughSeq;
        }
        buffer.Count.Should().Be(3, "nothing was acked, so nothing is trimmed");

        // A reconnect replays from the oldest unacked frame.
        var replay = await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, CancellationToken.None);
        replay.Message.ProbeResults.Results[0].TargetId.Should().Be("a");

        // Cumulative ack through the second frame trims a and b; only c remains.
        buffer.MarkAcked(replay.ThroughSeq + 1);
        buffer.Count.Should().Be(1);
        (await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, CancellationToken.None))
            .Message.ProbeResults.Results[0].TargetId.Should().Be("c");
    }

    [Fact]
    public async Task CoalesceMergesSameTypeUpToTheBoundary()
    {
        var buffer = new ResultBuffer();
        buffer.Enqueue(SnmpMessage("aa:bb:cc:dd:ee:ff"));
        buffer.Enqueue(SnmpMessage("11:22:33:44:55:66"));
        buffer.Enqueue(ProbeMessage("behind"));

        var frame = await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 500, CancellationToken.None);
        frame.Message.PayloadCase.Should().Be(AgentMessage.PayloadOneofCase.SnmpResults);
        frame.Message.SnmpResults.Interfaces.Should().HaveCount(2, "consecutive SNMP batches coalesce");
        buffer.MarkAcked(frame.ThroughSeq);

        var next = await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 500, CancellationToken.None);
        next.Message.PayloadCase.Should().Be(AgentMessage.PayloadOneofCase.ProbeResults);
        next.Message.ProbeResults.Results[0].TargetId.Should().Be("behind");
    }

    [Fact]
    public async Task CoalescePeekLeavesOriginalsIntactUntilAcked()
    {
        var buffer = new ResultBuffer();
        buffer.Enqueue(ProbeMessage("a"));
        buffer.Enqueue(ProbeMessage("b"));

        // Coalescing must not mutate or remove the buffered entries.
        var frame = await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 500, CancellationToken.None);
        frame.Message.ProbeResults.Results.Should().HaveCount(2);
        buffer.Count.Should().Be(2, "peek does not remove");

        buffer.MarkAcked(frame.ThroughSeq);
        buffer.Count.Should().Be(0, "the ack trims both coalesced frames");
    }

    [Fact]
    public async Task ByteCapDropsOldestFirst()
    {
        var single = ProbeMessage("x").CalculateSize();
        // Room for roughly three single-result messages.
        var buffer = new ResultBuffer(maxBytes: single * 3 + 1);

        for (var i = 0; i < 10; i++)
            buffer.Enqueue(ProbeMessage($"t{i}"));

        buffer.Count.Should().BeLessThan(10);
        buffer.DroppedTotal.Should().Be(10 - buffer.Count);
        buffer.ApproxBytes.Should().BeLessThanOrEqualTo(single * 3 + 1);

        // The survivors are the newest, still in FIFO order. maxSamples 1 reads
        // them one at a time.
        var survivors = new List<int>();
        while (buffer.Count > 0)
        {
            var frame = await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, CancellationToken.None);
            survivors.Add(int.Parse(frame.Message.ProbeResults.Results[0].TargetId[1..]));
            buffer.MarkAcked(frame.ThroughSeq);
        }
        survivors.Should().BeInAscendingOrder();
        survivors.Last().Should().Be(9);
    }

    [Fact]
    public async Task AgeCapDropsExpiredEntries()
    {
        var buffer = new ResultBuffer(maxAge: TimeSpan.FromMilliseconds(50));
        buffer.Enqueue(ProbeMessage("old"));
        await Task.Delay(150);
        buffer.Enqueue(ProbeMessage("new"));

        buffer.Count.Should().Be(1);
        buffer.DroppedTotal.Should().Be(1);
        (await NextAsync(buffer)).ProbeResults.Results[0].TargetId.Should().Be("new");
    }

    [Fact]
    public async Task EvictedPermitsDoNotStrandTheConsumer()
    {
        // Eviction removes entries without consuming semaphore permits; a consumer
        // waking to no unsent entry must keep waiting, then still receive the next.
        var buffer = new ResultBuffer(maxAge: TimeSpan.FromMilliseconds(50));
        buffer.Enqueue(ProbeMessage("doomed"));
        await Task.Delay(150);
        buffer.Enqueue(ProbeMessage("evictor")); // evicts "doomed" on enqueue

        (await NextAsync(buffer)).ProbeResults.Results[0].TargetId.Should().Be("evictor");

        var pending = buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, CancellationToken.None).AsTask();
        buffer.Enqueue(ProbeMessage("after"));
        (await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Message.ProbeResults.Results[0].TargetId.Should().Be("after");
    }

    [Fact]
    public void TakeDroppedCountResetsBetweenCalls()
    {
        var single = ProbeMessage("x").CalculateSize();
        var buffer = new ResultBuffer(maxBytes: single);
        buffer.Enqueue(ProbeMessage("a"));
        buffer.Enqueue(ProbeMessage("b")); // drops "a"

        buffer.TakeDroppedCount().Should().Be(1);
        buffer.TakeDroppedCount().Should().Be(0);
        buffer.DroppedTotal.Should().Be(1);
    }

    [Fact]
    public async Task ByteAccountingTracksEnqueueAndAck()
    {
        var buffer = new ResultBuffer();
        var message = ProbeMessage("sized");
        buffer.Enqueue(message);
        buffer.ApproxBytes.Should().Be(message.CalculateSize());

        var frame = await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, CancellationToken.None);
        buffer.ApproxBytes.Should().Be(message.CalculateSize(), "peek does not change accounting");
        buffer.MarkAcked(frame.ThroughSeq);
        buffer.ApproxBytes.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentProducersAndConsumerDeliverEverything()
    {
        var buffer = new ResultBuffer();
        const int perProducer = 200;
        var producers = Enumerable.Range(0, 4).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
                buffer.Enqueue(ProbeMessage($"p{p}-{i}"));
        })).ToArray();

        var seen = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (seen.Count < 4 * perProducer)
        {
            var frame = await buffer.TakeUnsentBatchAsync(afterSeq: 0, maxSamples: 1, cts.Token);
            seen.Add(frame.Message.ProbeResults.Results[0].TargetId);
            buffer.MarkAcked(frame.ThroughSeq);
        }

        await Task.WhenAll(producers);
        seen.Should().HaveCount(4 * perProducer);
        seen.Should().OnlyHaveUniqueItems();

        // Per-producer FIFO order survives interleaving.
        for (var p = 0; p < 4; p++)
        {
            var indices = seen.Where(id => id.StartsWith($"p{p}-"))
                .Select(id => int.Parse(id.Split('-')[1])).ToList();
            indices.Should().BeInAscendingOrder();
        }
    }
}
