namespace Compression.Core.Transforms;

/// <summary>
/// Write-side x86 BCJ decoder: bytes written to it are un-filtered (absolute CALL/JMP targets
/// back to relative) and passed on to the inner stream. The result equals
/// <see cref="BcjFilter.DecodeX86"/> over the whole stream, without holding the stream in memory.
/// </summary>
/// <remarks>
/// The filter can only decide a byte once the four after it are known, so up to four bytes stay
/// pending until <see cref="Finish"/> (or disposal) writes them through unchanged, as liblzma does
/// at the end of its input.
/// </remarks>
/// <param name="inner">Receives the decoded bytes.</param>
/// <param name="leaveOpen">Whether disposing this stream leaves <paramref name="inner"/> open.</param>
/// <param name="startOffset">The virtual address of the first byte, as used when encoding.</param>
public sealed class BcjX86DecodingStream(Stream inner, bool leaveOpen = false, uint startOffset = 0) : Stream {
  private const int BufferSize = 1 << 16;

  private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly byte[] _buffer = new byte[BufferSize];
  private BcjFilter.X86State _state = new(startOffset);
  private int _fill;
  private bool _finished;
  private bool _disposed;

  /// <inheritdoc/>
  public override bool CanRead => false;
  /// <inheritdoc/>
  public override bool CanSeek => false;
  /// <inheritdoc/>
  public override bool CanWrite => !this._disposed;
  /// <inheritdoc/>
  public override long Length => throw new NotSupportedException();
  /// <inheritdoc/>
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
  /// <inheritdoc/>
  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  /// <inheritdoc/>
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  /// <inheritdoc/>
  public override void SetLength(long value) => throw new NotSupportedException();
  /// <inheritdoc/>
  public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

  /// <inheritdoc/>
  public override void Write(ReadOnlySpan<byte> data) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (this._finished)
      throw new InvalidOperationException("The x86 filter stream has been finished.");
    while (!data.IsEmpty) {
      var take = Math.Min(BufferSize - this._fill, data.Length);
      data[..take].CopyTo(this._buffer.AsSpan(this._fill));
      this._fill += take;
      data = data[take..];
      if (this._fill == BufferSize)
        this.Drain();
    }
  }

  /// <summary>Passes on every byte the filter can already decide; the undecided tail stays.</summary>
  /// <inheritdoc/>
  public override void Flush() {
    this.Drain();
    this._inner.Flush();
  }

  /// <summary>Decodes and writes out everything, including the last bytes the filter leaves unchanged.</summary>
  public void Finish() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (this._finished)
      return;
    this.Drain();
    this._inner.Write(this._buffer, 0, this._fill);
    this._fill = 0;
    this._finished = true;
  }

  private void Drain() {
    var done = BcjFilter.TransformX86(this._buffer.AsSpan(0, this._fill), ref this._state, encode: false);
    if (done == 0)
      return;
    this._inner.Write(this._buffer, 0, done);
    this._buffer.AsSpan(done, this._fill - done).CopyTo(this._buffer);
    this._fill -= done;
  }

  /// <inheritdoc/>
  protected override void Dispose(bool disposing) {
    if (disposing && !this._disposed) {
      try {
        this.Finish();
      } finally {
        this._disposed = true;
        if (!leaveOpen)
          this._inner.Dispose();
      }
    }
    base.Dispose(disposing);
  }
}
