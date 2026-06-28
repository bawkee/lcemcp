namespace LceMcp;

// A write-only in-memory sink that throws before a write would cross the byte cap.
// MIME and ZIP decoders write through it so inaccurate declared sizes cannot bypass
// the attachment limits or leave a partially accepted buffer.
internal sealed class BoundedWriteStream : Stream
{
    private readonly MemoryStream _inner = new();
    private readonly long _maxBytes;

    public BoundedWriteStream(long maxBytes)
    {
        _maxBytes = maxBytes;
    }

    public byte[] ToArray() => _inner.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacity(count);
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureCapacity(buffer.Length);
        _inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        EnsureCapacity(buffer.Length);
        await _inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureCapacity(count);
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    private void EnsureCapacity(int incomingBytes)
    {
        if (_inner.Length + incomingBytes > _maxBytes)
            throw new AttachmentSizeLimitException(_maxBytes);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}

internal sealed class AttachmentSizeLimitException : IOException
{
    public AttachmentSizeLimitException(long maxBytes)
        : base($"Attachment exceeds the {maxBytes} byte download limit.")
    {
    }
}
