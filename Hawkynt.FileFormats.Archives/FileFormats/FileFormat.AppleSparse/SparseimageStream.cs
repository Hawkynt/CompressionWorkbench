#pragma warning disable CS1591

namespace FileFormat.AppleSparse;

/// <summary>
/// Read-only seekable virtual-disk view over a sparseimage. Unallocated bands
/// read as zeros; the stream surface is the inner virtual size, not the
/// physical sparseimage file length.
/// </summary>
public sealed class SparseimageStream : Stream {
  private readonly SparseimageReader _reader;
  private readonly bool _leaveOpen;
  private long _position;

  /// <summary>Wraps an already-constructed reader.</summary>
  public SparseimageStream(SparseimageReader reader, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(reader);
    this._reader = reader;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Tries to open a <see cref="SparseimageStream"/> over the given backing
  /// stream. Returns <c>null</c> if the magic doesn't match or the header is
  /// malformed.
  /// </summary>
  public static SparseimageStream? TryOpen(Stream backing) {
    ArgumentNullException.ThrowIfNull(backing);
    try {
      var savedPos = backing.CanSeek ? backing.Position : 0;
      try {
        var reader = new SparseimageReader(backing, leaveOpen: true);
        return new SparseimageStream(reader, leaveOpen: false);
      } finally {
        if (backing.CanSeek) backing.Position = savedPos;
      }
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Gets a value indicating whether can read.
  /// </summary>
  public override bool CanRead => true;
  /// <summary>
  /// Gets a value indicating whether can seek.
  /// </summary>
  public override bool CanSeek => true;
  /// <summary>
  /// Gets a value indicating whether can write.
  /// </summary>
  public override bool CanWrite => false;
  /// <summary>
  /// Gets the length.
  /// </summary>
  public override long Length => this._reader.VirtualSize;
  /// <summary>
  /// Gets or sets the position.
  /// </summary>
  public override long Position {
    get => this._position;
    set {
      if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
      this._position = value;
    }
  }

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    if (this._position >= this._reader.VirtualSize) return 0;
    var n = this._reader.Read(this._position, buffer.AsSpan(offset, count));
    this._position += n;
    return n;
  }

  /// <summary>
  /// Performs the seek operation.
  /// </summary>
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

  /// <summary>
  /// Performs the flush operation.
  /// </summary>
  public override void Flush() { /* read-only */ }
  /// <summary>
  /// Sets the length.
  /// </summary>
  public override void SetLength(long value) => throw new NotSupportedException();
  /// <summary>
  /// Writes the value to the supplied output.
  /// </summary>
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  protected override void Dispose(bool disposing) {
    if (disposing && !this._leaveOpen)
      this._reader.Dispose();
    base.Dispose(disposing);
  }
}
