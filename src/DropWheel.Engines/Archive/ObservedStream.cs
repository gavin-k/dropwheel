namespace DropWheel.Engines.Archive;

/// <summary>
/// Read-only pass-through that checks for cancellation and reports the size of every read, so a long
/// synchronous copy inside a library can be followed and stopped. Disposing it disposes the inner stream.
/// </summary>
internal sealed class ObservedStream(Stream inner, Action<int> read, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var n = inner.Read(buffer);
        read(n);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
