using System.Buffers.Binary;

namespace FileFormat.BochsDisk;

/// <summary>
/// Read-only, seekable view of the guest disk reconstructed from a Bochs
/// "Redolog" growing/undoable image. Extents are resolved lazily through the
/// catalog on each read — no full flat copy of the disk is materialised, so the
/// view is not bounded by the 2 GiB <see cref="Array"/> limit. Unallocated
/// extents (catalog entry <c>0xFFFFFFFF</c>) read back as zero.
/// </summary>
internal sealed class BochsGuestDiskStream : Stream {
  private readonly Stream _source;
  private readonly bool _leaveOpen;
  private readonly uint[] _catalog;
  private readonly long _extentRegion;
  private readonly long _perExtent;
  private readonly long _bitmapBytes;
  private readonly long _extentBytes;
  private readonly long _length;
  private long _position;

  public BochsGuestDiskStream(Stream source, uint[] catalog, long extentRegion,
      long bitmapBytes, long extentBytes, long diskSize, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(catalog);
    if (!source.CanSeek) throw new ArgumentException("Underlying stream must be seekable.", nameof(source));
    this._source = source;
    this._leaveOpen = leaveOpen;
    this._catalog = catalog;
    this._extentRegion = extentRegion;
    this._bitmapBytes = bitmapBytes;
    this._extentBytes = extentBytes;
    this._perExtent = bitmapBytes + extentBytes;
    this._length = diskSize;
  }

  public override bool CanRead => true;
  public override bool CanSeek => true;
  public override bool CanWrite => false;
  public override long Length => this._length;

  public override long Position {
    get => this._position;
    set {
      ArgumentOutOfRangeException.ThrowIfNegative(value);
      this._position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    if (this._extentBytes <= 0) return 0;
    if (this._position >= this._length) return 0;

    var remaining = (int)Math.Min(count, this._length - this._position);
    var produced = 0;
    while (produced < remaining) {
      var extentIndex = this._position / this._extentBytes;
      var within = (int)(this._position % this._extentBytes);
      var chunk = (int)Math.Min(remaining - produced, this._extentBytes - within);

      var slot = extentIndex < this._catalog.Length ? this._catalog[extentIndex] : 0xFFFFFFFFu;
      if (slot != 0xFFFFFFFFu) {
        var src = this._extentRegion + slot * this._perExtent + this._bitmapBytes + within;
        if (src >= 0 && src + chunk <= this._source.Length) {
          this._source.Position = src;
          var got = 0;
          while (got < chunk) {
            var n = this._source.Read(buffer, offset + produced + got, chunk - got);
            if (n <= 0) break;
            got += n;
          }
          if (got < chunk)
            Array.Clear(buffer, offset + produced + got, chunk - got);
        } else {
          Array.Clear(buffer, offset + produced, chunk);
        }
      } else {
        Array.Clear(buffer, offset + produced, chunk);
      }

      produced += chunk;
      this._position += chunk;
    }
    return produced;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    this._position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    return this._position;
  }

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  protected override void Dispose(bool disposing) {
    if (disposing && !this._leaveOpen) this._source.Dispose();
    base.Dispose(disposing);
  }

  /// <summary>
  /// Reads and validates the header, returning a lazy guest-disk stream, or
  /// <c>null</c> when the image is not a reconstructable Bochs Redolog disk.
  /// </summary>
  public static BochsGuestDiskStream? TryOpen(Stream source) {
    const int magicLen = 32, typeLen = 16, subtypeLen = 16;
    if (!source.CanSeek) return null;
    source.Position = 0;
    Span<byte> head = stackalloc byte[magicLen + typeLen + subtypeLen + 24];
    if (!TryReadExact(source, head)) return null;
    if (!head[..magicLen].StartsWith("Bochs Virtual HD Image"u8)) return null;

    var p = magicLen + typeLen + subtypeLen;
    var version = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(p, 4));
    var catalogEntries = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(p + 4, 4));
    var bitmapBytes = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(p + 8, 4));
    var extentBytes = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(p + 12, 4));
    ulong diskSize = 0;
    if (version >= 0x00020000)
      diskSize = BinaryPrimitives.ReadUInt64BigEndian(head.Slice(p + 16, 8));
    else if (extentBytes > 0)
      diskSize = (ulong)catalogEntries * extentBytes;

    if (catalogEntries == 0 || extentBytes == 0 || diskSize == 0) return null;
    if ((long)catalogEntries * 4 > 256L * 1024 * 1024) return null;

    const long catalogOffset = 512;
    var catalogBytes = (long)catalogEntries * 4;
    if (catalogOffset + catalogBytes > source.Length) return null;

    source.Position = catalogOffset;
    var catBuf = new byte[catalogBytes];
    if (!TryReadExact(source, catBuf)) return null;
    var catalog = new uint[catalogEntries];
    for (var i = 0; i < catalog.Length; ++i)
      catalog[i] = BinaryPrimitives.ReadUInt32BigEndian(catBuf.AsSpan(i * 4, 4));

    var extentRegion = (catalogOffset + catalogBytes + 511) & ~511L;
    return new BochsGuestDiskStream(source, catalog, extentRegion, bitmapBytes, extentBytes, (long)diskSize);
  }

  private static bool TryReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }
}
