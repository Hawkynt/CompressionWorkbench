using System.Buffers.Binary;

namespace FileFormat.ParallelsHdd;

/// <summary>
/// Read-only, seekable view of the guest disk reconstructed from a Parallels
/// expanding image (<c>.hds</c>). Blocks are resolved lazily through the BAT on
/// each read — no full flat copy of the disk is materialised in memory, so the
/// view is not bounded by the 2 GiB <see cref="Array"/> limit and can back an
/// arbitrarily large guest disk. Unallocated blocks (BAT entry 0) read back as
/// zero.
/// </summary>
internal sealed class ParallelsGuestDiskStream : Stream {
  private const int SectorSize = 512;

  private readonly Stream _source;
  private readonly bool _leaveOpen;
  private readonly uint[] _bat;
  private readonly long _blockBytes;
  private readonly long _length;
  private long _position;

  /// <summary>
  /// Builds a lazy guest-disk stream over <paramref name="source"/>.
  /// </summary>
  /// <param name="source">The underlying <c>.hds</c> stream (seekable).</param>
  /// <param name="bat">Block allocation table: per-block start sector, 0 = unallocated.</param>
  /// <param name="blockSizeSectors">Sectors per BAT block.</param>
  /// <param name="imageSizeSectors">Total guest-disk size in sectors.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="source"/> open on dispose.</param>
  public ParallelsGuestDiskStream(Stream source, uint[] bat, uint blockSizeSectors,
      uint imageSizeSectors, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(bat);
    if (!source.CanSeek) throw new ArgumentException("Underlying stream must be seekable.", nameof(source));
    this._source = source;
    this._leaveOpen = leaveOpen;
    this._bat = bat;
    this._blockBytes = (long)blockSizeSectors * SectorSize;
    this._length = (long)imageSizeSectors * SectorSize;
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
    if (this._blockBytes <= 0) return 0;
    if (this._position >= this._length) return 0;

    var remaining = (int)Math.Min(count, this._length - this._position);
    var produced = 0;
    while (produced < remaining) {
      var blockIndex = this._position / this._blockBytes;
      var within = (int)(this._position % this._blockBytes);
      var chunk = (int)Math.Min(remaining - produced, this._blockBytes - within);

      if (blockIndex < this._bat.Length && this._bat[blockIndex] != 0) {
        var src = (long)this._bat[blockIndex] * SectorSize + within;
        if (src + chunk <= this._source.Length) {
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
        // Unallocated block (or BAT shorter than the disk) reads back as zero.
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
  /// <c>null</c> when the image is not a reconstructable Parallels disk.
  /// </summary>
  public static ParallelsGuestDiskStream? TryOpen(Stream source) {
    if (!source.CanSeek || source.Length < 64) return null;
    source.Position = 0;
    Span<byte> h = stackalloc byte[64];
    if (!TryReadExact(source, h)) return null;

    var known = h[..16].StartsWith("WithoutFreeSpace"u8) || h[..15].StartsWith("WithouFreSpaExt"u8)
                || h[..13].StartsWith("WithFreeSpace"u8);
    if (!known) return null;

    var blockSizeSectors = BinaryPrimitives.ReadUInt32LittleEndian(h[28..32]);
    var imageSizeSectors = BinaryPrimitives.ReadUInt32LittleEndian(h[32..36]);
    var batEntries = BinaryPrimitives.ReadUInt32LittleEndian(h[36..40]);
    if (blockSizeSectors == 0 || imageSizeSectors == 0 || batEntries == 0) return null;
    if ((long)batEntries * 4 > 256L * 1024 * 1024) return null;

    const long batOffset = 64;
    var batByteLen = (long)batEntries * 4;
    if (batOffset + batByteLen > source.Length) return null;

    var buf = new byte[batByteLen];
    source.Position = batOffset;
    if (!TryReadExact(source, buf)) return null;
    var bat = new uint[batEntries];
    for (var i = 0; i < bat.Length; ++i)
      bat[i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4, 4));

    return new ParallelsGuestDiskStream(source, bat, blockSizeSectors, imageSizeSectors);
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
