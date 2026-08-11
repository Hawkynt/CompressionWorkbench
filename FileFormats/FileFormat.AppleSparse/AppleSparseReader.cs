#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.AppleSparse;

/// <summary>
/// Reads the band-allocation table from an Apple sparseimage container
/// (<c>hdiutil create -type SPARSE</c>) and exposes each allocated band
/// as a synthetic entry. The container layout used here matches the
/// publicly-known geometry: a 4 096-byte header at offset 0 carrying the
/// <c>sprs</c> magic and big-endian fields, followed by physical bands of
/// <c>sectors_per_band * 512</c> bytes each.
///
/// <para>This is a band-level reader only — it does not parse the inner
/// HFS+/APFS filesystem that lives inside the bands. Inner-FS extraction
/// remains the responsibility of <c>FileSystem.HfsPlus</c> /
/// <c>FileSystem.Apfs</c>, which can be invoked separately on the bytes
/// produced by stitching the allocated bands together.</para>
/// </summary>
public sealed class AppleSparseReader {

  /// <summary>"sprs" — magic at byte offset 0 of every sparseimage.</summary>
  public static readonly byte[] Magic = "sprs"u8.ToArray();

  /// <summary>Size of the fixed header region (band-allocation table included).</summary>
  public const int HeaderSize = 4096;

  /// <summary>One CD/HFS+ sector — fixed at 512 bytes by the sparseimage spec.</summary>
  public const int SectorBytes = 512;

  /// <summary>Maximum number of logical-to-physical band-table entries that
  /// fit in the 4 096-byte header after the 32-byte preamble.</summary>
  public const int MaxBandTableEntries = (HeaderSize - HeaderPreambleSize) / 4;

  /// <summary>Size of the fixed preamble before the band-table:
  /// magic (4) + version (4) + sectors_per_band (4) + flags (4) +
  /// max_logical_bands (4) + next_physical_slot (4) + allocated_count (4)
  /// + reserved (4) = 32.</summary>
  public const int HeaderPreambleSize = 32;

  /// <summary>A single band entry surfaced by the reader.</summary>
  public sealed record class BandEntry(
    int LogicalBandIndex,
    int PhysicalSlotIndex,
    long ByteOffset,
    int Size);

  /// <summary>Container metadata parsed from the header preamble.</summary>
  public sealed record class Container(
    int Version,
    int SectorsPerBand,
    int Flags,
    int MaxLogicalBands,
    int NextPhysicalSlot,
    int AllocatedCount,
    int BandSize,
    IReadOnlyList<BandEntry> Bands);

  /// <summary>
  /// Parses the band-allocation table at the start of <paramref name="image"/>.
  /// Returns <c>null</c> when the container is too small or the
  /// <c>sprs</c> magic is missing, so callers can probe without throwing.
  /// </summary>
  public static Container? TryRead(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek) return null;
    if (image.Length < HeaderSize) return null;

    image.Position = 0;
    var header = new byte[HeaderSize];
    var read = 0;
    while (read < HeaderSize) {
      var n = image.Read(header, read, HeaderSize - read);
      if (n <= 0) return null;
      read += n;
    }

    if (header[0] != Magic[0] || header[1] != Magic[1] ||
        header[2] != Magic[2] || header[3] != Magic[3])
      return null;

    var version = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
    var sectorsPerBand = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
    var flags = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));
    var maxLogicalBands = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
    var nextPhysicalSlot = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
    var allocatedCount = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24, 4));

    if (sectorsPerBand <= 0 || sectorsPerBand > 65536) return null;
    if (maxLogicalBands < 0 || maxLogicalBands > MaxBandTableEntries) return null;

    var bandSize = sectorsPerBand * SectorBytes;
    var bands = new List<BandEntry>(allocatedCount);

    for (var logical = 0; logical < maxLogicalBands; logical++) {
      var entryOffset = HeaderPreambleSize + logical * 4;
      var physicalSlot = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(entryOffset, 4));
      if (physicalSlot == 0) continue; // unallocated logical band
      var byteOffset = (long)HeaderSize + (long)(physicalSlot - 1) * bandSize;
      bands.Add(new BandEntry(logical, physicalSlot, byteOffset, bandSize));
    }

    return new Container(
      Version: version,
      SectorsPerBand: sectorsPerBand,
      Flags: flags,
      MaxLogicalBands: maxLogicalBands,
      NextPhysicalSlot: nextPhysicalSlot,
      AllocatedCount: allocatedCount,
      BandSize: bandSize,
      Bands: bands);
  }

  /// <summary>
  /// Reads the bytes of one band into a fresh buffer. The buffer length
  /// matches the band size (a band may be sparse-zero at any byte; the
  /// caller is responsible for interpreting that).
  /// </summary>
  public static byte[] ReadBand(Stream image, BandEntry band) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(band);
    var buf = new byte[band.Size];
    image.Position = band.ByteOffset;
    var total = 0;
    while (total < buf.Length) {
      var n = image.Read(buf, total, buf.Length - total);
      if (n <= 0) break;
      total += n;
    }
    return buf;
  }

  /// <summary>
  /// Returns the synthetic entry name for the logical band index. Used
  /// by descriptors when listing/extracting sparseimage contents.
  /// </summary>
  public static string FormatBandName(int logicalBandIndex)
    => System.Globalization.CultureInfo.InvariantCulture is var ci
       ? string.Create(ci, $"band-{logicalBandIndex:D4}.bin")
       : $"band-{logicalBandIndex:D4}.bin";
}
