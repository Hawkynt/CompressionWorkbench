#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.AppleSparse;

/// <summary>
/// Reader for Apple sparseimage files (single-file variant produced by
/// <c>hdiutil create -type SPARSE</c>). Parses the <c>sprs</c> header, derives
/// sectors-per-band and total virtual size, then materialises the virtual disk
/// from the band-allocation table.
/// </summary>
/// <remarks>
/// <para>
/// The sparseimage on-disk layout used here is the historically-documented
/// v1/v2 form: a 4096-byte primary header carrying <c>sprs</c>, version,
/// sectors_per_band, total_sectors (high/low halves), num_bands and
/// header_size, followed immediately by a BAT of <c>num_bands</c> 32-bit
/// big-endian entries (0 = unallocated, otherwise a 1-based physical band
/// index). Allocated bands begin at the first 512-byte boundary after the
/// BAT and are written sequentially in BAT-entry order.
/// </para>
/// <para>
/// Real-world sparseimage files produced by Apple's <c>diskimages-helper</c>
/// daemon may use chained extension headers when the BAT grows past the
/// primary header; this reader handles the common single-header case and
/// surfaces images written by <see cref="SparseimageWriter"/> losslessly.
/// External (hdiutil-produced) images with extension headers fall back to
/// detection-only via the descriptor.
/// </para>
/// </remarks>
public sealed class SparseimageReader : IDisposable {
  internal const int HeaderSize = 4096;
  internal const int SectorSize = 512;
  internal static readonly byte[] Magic = "sprs"u8.ToArray();

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly uint[] _bat;
  private readonly int _sectorsPerBand;
  private readonly long _virtualSize;
  private readonly long _firstBandOffset;
  private readonly int _bandSizeBytes;

  /// <summary>Sectors per band (each sector = 512 bytes).</summary>
  public int SectorsPerBand => this._sectorsPerBand;

  /// <summary>Total virtual disk size in bytes.</summary>
  public long VirtualSize => this._virtualSize;

  /// <summary>Number of bands in the BAT (allocated + sparse).</summary>
  public int BandCount => this._bat.Length;

  /// <summary>
  /// Opens the sparseimage. Throws <see cref="InvalidDataException"/> on bad
  /// magic, truncated header, or implausible band geometry.
  /// </summary>
  public SparseimageReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    this._stream = stream;
    this._leaveOpen = leaveOpen;

    if (stream.Length < HeaderSize)
      throw new InvalidDataException("sparseimage: file too small (header is 4096 bytes).");

    stream.Position = 0;
    Span<byte> hdr = stackalloc byte[HeaderSize];
    stream.ReadExactly(hdr);

    if (!hdr[..4].SequenceEqual(Magic))
      throw new InvalidDataException("sparseimage: invalid magic (expected 'sprs').");

    var version = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..]);
    if (version is 0 or > 8)
      throw new InvalidDataException($"sparseimage: implausible version {version}.");

    this._sectorsPerBand = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[8..]);
    if (this._sectorsPerBand <= 0 || this._sectorsPerBand > 0x100000)
      throw new InvalidDataException(
        $"sparseimage: implausible sectors_per_band {this._sectorsPerBand}.");

    this._bandSizeBytes = this._sectorsPerBand * SectorSize;

    // flags @ 12 (unused)
    var totalSectorsHigh = BinaryPrimitives.ReadUInt32BigEndian(hdr[16..]);
    var totalSectorsLow = BinaryPrimitives.ReadUInt32BigEndian(hdr[20..]);
    var totalSectors = ((long)totalSectorsHigh << 32) | totalSectorsLow;
    this._virtualSize = totalSectors * SectorSize;

    var numBands = BinaryPrimitives.ReadUInt32BigEndian(hdr[24..]);
    var headerSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[28..]);
    if (headerSize != HeaderSize)
      throw new InvalidDataException(
        $"sparseimage: unexpected header_size {headerSize} (this reader supports the single-header variant only).");

    if (numBands > 0x10000000)
      throw new InvalidDataException($"sparseimage: implausible num_bands {numBands}.");

    // BAT begins immediately after the header
    var batBytes = (long)numBands * 4;
    if (HeaderSize + batBytes > stream.Length)
      throw new InvalidDataException("sparseimage: BAT extends past EOF.");

    this._bat = new uint[numBands];
    if (numBands > 0) {
      var buf = new byte[batBytes];
      stream.Position = HeaderSize;
      stream.ReadExactly(buf);
      for (var i = 0; i < numBands; i++)
        this._bat[i] = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i * 4, 4));
    }

    // First physical band data starts at the first SectorSize-aligned offset
    // after the BAT (which lives directly after the header).
    var batEnd = HeaderSize + batBytes;
    this._firstBandOffset = (batEnd + SectorSize - 1) & ~(long)(SectorSize - 1);
  }

  /// <summary>
  /// Reads <paramref name="destination"/>.<see cref="Span{T}.Length"/> bytes
  /// from virtual offset <paramref name="virtualOffset"/> into
  /// <paramref name="destination"/>. Unallocated bands return zero bytes.
  /// Returns the number of bytes actually filled (always the requested length
  /// when <c>virtualOffset + length &lt;= VirtualSize</c>).
  /// </summary>
  public int Read(long virtualOffset, Span<byte> destination) {
    if (virtualOffset < 0) throw new ArgumentOutOfRangeException(nameof(virtualOffset));
    if (virtualOffset >= this._virtualSize) return 0;

    var remaining = (int)Math.Min(destination.Length, this._virtualSize - virtualOffset);
    var total = 0;
    while (remaining > 0) {
      var bandIdx = (int)(virtualOffset / this._bandSizeBytes);
      var bandOff = (int)(virtualOffset % this._bandSizeBytes);
      var take = Math.Min(remaining, this._bandSizeBytes - bandOff);

      if (bandIdx >= this._bat.Length || this._bat[bandIdx] == 0) {
        destination.Slice(total, take).Clear();
      } else {
        var physBandIdx = this._bat[bandIdx] - 1; // 1-based -> 0-based
        var physOffset = this._firstBandOffset + (long)physBandIdx * this._bandSizeBytes + bandOff;
        if (physOffset + take > this._stream.Length) {
          // Truncated band — fill missing tail with zeros, copy what we can.
          var available = (int)Math.Max(0, this._stream.Length - physOffset);
          if (available > 0) {
            this._stream.Position = physOffset;
            this._stream.ReadExactly(destination.Slice(total, available));
          }
          if (available < take)
            destination.Slice(total + available, take - available).Clear();
        } else {
          this._stream.Position = physOffset;
          this._stream.ReadExactly(destination.Slice(total, take));
        }
      }

      virtualOffset += take;
      total += take;
      remaining -= take;
    }
    return total;
  }

  /// <summary>Materialises the full virtual disk as a byte array.</summary>
  public byte[] ExtractDisk() {
    var buf = new byte[this._virtualSize];
    if (this._virtualSize > 0)
      this.Read(0, buf);
    return buf;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
