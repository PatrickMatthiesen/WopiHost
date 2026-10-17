using Microsoft.AspNetCore.Http;

namespace WopiHost.Core.Infrastructure;

/// <summary>Enforces the host's body limit while staging, including bodies without Content-Length.</summary>
internal sealed class MaxFileSizeReadStream(Stream source, long maximumBytes) : Stream
{
    private long _read;
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => Count(source.Read(buffer));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    private int Count(int count)
    {
        if (count > maximumBytes - _read)
            throw new BadHttpRequestException("The file exceeds the configured maximum size.", StatusCodes.Status413PayloadTooLarge);
        _read += count;
        return count;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
