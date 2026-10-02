namespace Compression.Core.Streams;

/// <summary>
/// Read-only forward stream over another stream that lets bytes already read be handed back,
/// so a parser can look ahead on a stream that cannot seek.
/// </summary>
/// <remarks>Disposing this wrapper does not dispose the inner stream.</remarks>
/// <param name="inner">The stream to read from.</param>
internal sealed class PushbackStream(Stream inner) : Stream {
  private byte[] _pending = [];
  private int _pendingPos;

  /// <summary>Puts <paramref name="data"/> back in front of whatever is still unread.</summary>
  public void Unread(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return;

    var rest = this._pending.AsSpan(this._pendingPos);
    var merged = new byte[data.Length + rest.Length];
    data.CopyTo(merged);
    rest.CopyTo(merged.AsSpan(data.Length));
    this._pending = merged;
    this._pendingPos = 0;
  }

  /// <inheritdoc />
  public override int Read(Span<byte> buffer) {
    var pending = this._pending.Length - this._pendingPos;
    if (pending == 0)
      return inner.Read(buffer);

    var n = Math.Min(pending, buffer.Length);
    this._pending.AsSpan(this._pendingPos, n).CopyTo(buffer);
    this._pendingPos += n;
    return n;
  }

  /// <inheritdoc />
  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  /// <inheritdoc />
  public override bool CanRead => true;

  /// <inheritdoc />
  public override bool CanSeek => false;

  /// <inheritdoc />
  public override bool CanWrite => false;

  /// <inheritdoc />
  public override long Length => throw new NotSupportedException();

  /// <inheritdoc />
  public override long Position {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  /// <inheritdoc />
  public override void Flush() { }

  /// <inheritdoc />
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  /// <inheritdoc />
  public override void SetLength(long value) => throw new NotSupportedException();

  /// <inheritdoc />
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
