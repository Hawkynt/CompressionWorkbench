#pragma warning disable CS1591

namespace Compression.Benchmarks;

/// <summary>
/// Transparent stream wrapper that counts total bytes read and written.
/// Used by modifier benchmarks to verify O(touched bytes) IO ratio.
/// </summary>
internal sealed class ByteCountingStream : Stream {
  private readonly Stream _inner;

  public ByteCountingStream(Stream inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

  public long BytesRead { get; private set; }
  public long BytesWritten { get; private set; }

  public override bool CanRead => _inner.CanRead;
  public override bool CanSeek => _inner.CanSeek;
  public override bool CanWrite => _inner.CanWrite;
  public override long Length => _inner.Length;

  public override long Position {
    get => _inner.Position;
    set => _inner.Position = value;
  }

  public override void Flush() => _inner.Flush();

  public override int Read(byte[] buffer, int offset, int count) {
    var n = _inner.Read(buffer, offset, count);
    BytesRead += n;
    return n;
  }

  public override int Read(Span<byte> buffer) {
    var n = _inner.Read(buffer);
    BytesRead += n;
    return n;
  }

  public override void Write(byte[] buffer, int offset, int count) {
    _inner.Write(buffer, offset, count);
    BytesWritten += count;
  }

  public override void Write(ReadOnlySpan<byte> buffer) {
    _inner.Write(buffer);
    BytesWritten += buffer.Length;
  }

  public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
  public override void SetLength(long value) => _inner.SetLength(value);

  protected override void Dispose(bool disposing) {
    if (disposing) _inner.Dispose();
    base.Dispose(disposing);
  }
}
