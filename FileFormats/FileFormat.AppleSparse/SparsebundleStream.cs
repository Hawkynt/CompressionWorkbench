#pragma warning disable CS1591

namespace FileFormat.AppleSparse;

/// <summary>
/// Read-only seekable virtual-disk view over a sparsebundle directory.
/// Missing bands surface as zero bytes.
/// </summary>
public sealed class SparsebundleStream : Stream {
  private readonly SparsebundleReader _reader;
  private long _position;

  public SparsebundleStream(SparsebundleReader reader) {
    ArgumentNullException.ThrowIfNull(reader);
    this._reader = reader;
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => this._reader.VirtualSize;
  public override long Position {
    get => this._position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    if (this._position >= this._reader.VirtualSize) return 0;
    var n = this._reader.Read(this._position, buffer.AsSpan(offset, count));
    this._position += n;
    return n;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    var newPos = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._reader.VirtualSize + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    if (newPos < 0) throw new IOException("Seek before beginning of stream.");
    this._position = newPos;
    return this._position;
  }

  public override void Flush() { /* read-only */ }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
