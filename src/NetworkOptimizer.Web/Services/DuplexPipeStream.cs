using System.IO.Pipelines;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// One end of an in-memory, full-duplex byte stream: what one end writes, the other reads.
/// Disposing an end completes both of its directions, so the other end reads end-of-stream.
/// </summary>
internal sealed class DuplexPipeStream : Stream
{
    // Room for 32 tunnel frames per direction before a writer waits.
    private static readonly PipeOptions Options = new(
        pauseWriterThreshold: 1024 * 1024,
        resumeWriterThreshold: 512 * 1024,
        useSynchronizationContext: false);

    private readonly Stream _reader;
    private readonly Stream _writer;

    private DuplexPipeStream(PipeReader reader, PipeWriter writer)
    {
        _reader = reader.AsStream();
        _writer = writer.AsStream();
    }

    /// <summary>Two connected ends.</summary>
    public static (DuplexPipeStream A, DuplexPipeStream B) CreatePair()
    {
        var aToB = new Pipe(Options);
        var bToA = new Pipe(Options);
        return (new DuplexPipeStream(bToA.Reader, aToB.Writer), new DuplexPipeStream(aToB.Reader, bToA.Writer));
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => _reader.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _reader.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _reader.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _writer.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _writer.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _writer.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _writer.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
            _writer.Dispose();
        }
        base.Dispose(disposing);
    }
}
