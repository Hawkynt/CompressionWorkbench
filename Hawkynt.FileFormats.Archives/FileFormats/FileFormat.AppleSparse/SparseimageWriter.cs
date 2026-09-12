#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.AppleSparse;

/// <summary>
/// Synthetic sparseimage writer used for round-trip testing and the
/// <see cref="SparseimageFormatDescriptor"/> WORM <c>Create</c> path. Produces
/// a single-header sparseimage whose all-zero bands are stored unallocated in
/// the BAT (zero entries) and whose non-zero bands are packed sequentially
/// after the BAT.
/// </summary>
/// <remarks>
/// Output matches <see cref="SparseimageReader"/> precisely (self-consistent
/// round-trip). It is <em>not</em> claimed to be byte-identical with
/// <c>hdiutil</c> output — the documented header schema varies across Apple
/// releases. The <c>sprs</c> magic and the high-level band layout are
/// conformant.
/// </remarks>
public sealed class SparseimageWriter {
  private const int DefaultSectorsPerBand = 2048; // 1 MB band

  private byte[] _diskData = [];
  private int _sectorsPerBand = DefaultSectorsPerBand;

  /// <summary>Sets the raw virtual-disk contents (padded to band size on write).</summary>
  public void SetDiskData(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._diskData = data;
  }

  /// <summary>Overrides the band geometry; default 2048 sectors (1 MB).</summary>
  public void SetSectorsPerBand(int sectorsPerBand) {
    if (sectorsPerBand <= 0 || sectorsPerBand > 0x100000)
      throw new ArgumentOutOfRangeException(nameof(sectorsPerBand));
    this._sectorsPerBand = sectorsPerBand;
  }

  /// <summary>Builds the sparseimage bytes.</summary>
  public byte[] Build() {
    var bandBytes = this._sectorsPerBand * SparseimageReader.SectorSize;
    // Pad disk size up to band boundary
    var paddedLen = (this._diskData.Length + bandBytes - 1) / bandBytes * bandBytes;
    var numBands = paddedLen / bandBytes;
    var totalSectors = (long)paddedLen / SparseimageReader.SectorSize;

    // BAT: 0 = unallocated, otherwise 1-based physical band index
    var bat = new uint[numBands];
    var physBands = new List<byte[]>();
    for (var i = 0; i < numBands; i++) {
      var srcOff = i * bandBytes;
      var band = new byte[bandBytes];
      var copy = Math.Min(bandBytes, this._diskData.Length - srcOff);
      if (copy > 0) Array.Copy(this._diskData, srcOff, band, 0, copy);

      if (IsAllZero(band)) {
        bat[i] = 0;
      } else {
        physBands.Add(band);
        bat[i] = (uint)physBands.Count; // 1-based
      }
    }

    var batBytes = numBands * 4L;
    var firstBandOffset = (SparseimageReader.HeaderSize + batBytes + SparseimageReader.SectorSize - 1)
                          & ~(long)(SparseimageReader.SectorSize - 1);

    var totalLen = firstBandOffset + (long)physBands.Count * bandBytes;
    var result = new byte[totalLen];

    // Header
    SparseimageReader.Magic.CopyTo(result.AsSpan(0));
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 1u);                       // version
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)this._sectorsPerBand);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), 0u);                      // flags
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), (uint)(totalSectors >> 32));
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), (uint)(totalSectors & 0xFFFFFFFFu));
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(24), (uint)numBands);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(28), (uint)SparseimageReader.HeaderSize);

    // BAT
    for (var i = 0; i < numBands; i++)
      BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(SparseimageReader.HeaderSize + i * 4), bat[i]);

    // Physical bands
    var pos = firstBandOffset;
    foreach (var band in physBands) {
      band.CopyTo(result.AsSpan((int)pos));
      pos += bandBytes;
    }

    return result;
  }

  private static bool IsAllZero(ReadOnlySpan<byte> data) {
    for (var i = 0; i < data.Length; i++)
      if (data[i] != 0) return false;
    return true;
  }
}
