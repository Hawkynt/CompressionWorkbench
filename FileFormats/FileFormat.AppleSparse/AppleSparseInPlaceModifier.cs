#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;

namespace FileFormat.AppleSparse;

/// <summary>
/// In-place band-rewrite modifier for an Apple sparseimage container.
/// Operates at the fixed byte offset
/// <c>HeaderSize + (band_table[logical] - 1) * band_size</c>, where
/// <c>band_table</c> lives at the back half of the 4 096-byte header and
/// the band size is <c>sectors_per_band * 512</c>. Existing, allocated
/// bands are rewritten in place; logical bands that aren't yet allocated
/// are given a fresh physical slot at end-of-file and their band-table
/// entry is updated.
///
/// <para><b>Scope.</b> This operates at the band level only. The bytes
/// inside a band are an opaque payload — typically an HFS+ or APFS
/// fragment — and mutation of that inner filesystem is delegated to
/// <c>FileSystem.HfsPlus</c> / <c>FileSystem.Apfs</c>. Synthetic entry
/// names of the form <c>band-NNNN.bin</c> address one logical band.</para>
///
/// <para><b>True in-place.</b> Writes touch only the targeted band's
/// payload window plus, for newly-allocated bands, the 4-byte band-table
/// slot. All other bytes — the header preamble, every other band-table
/// slot, every untouched band — stay byte-identical at their original
/// byte offsets.</para>
/// </summary>
public static class AppleSparseInPlaceModifier {

  private const int HeaderSize = AppleSparseReader.HeaderSize;
  private const int HeaderPreambleSize = AppleSparseReader.HeaderPreambleSize;
  private const int MaxBandTableEntries = AppleSparseReader.MaxBandTableEntries;
  private const int SectorBytes = AppleSparseReader.SectorBytes;

  /// <summary>
  /// Produces a minimal, valid sparseimage container with zero allocated
  /// bands. Used by tests and by descriptors that need an empty starting
  /// state. <paramref name="sectorsPerBand"/> defaults to 2 048 (1 MiB
  /// bands), matching the <c>hdiutil</c> default.
  /// </summary>
  public static byte[] BuildEmptyContainer(int sectorsPerBand = 2048, int maxLogicalBands = 256) {
    if (sectorsPerBand <= 0 || sectorsPerBand > 65536)
      throw new ArgumentOutOfRangeException(nameof(sectorsPerBand));
    if (maxLogicalBands < 0 || maxLogicalBands > MaxBandTableEntries)
      throw new ArgumentOutOfRangeException(nameof(maxLogicalBands));

    var header = new byte[HeaderSize];
    AppleSparseReader.Magic.CopyTo(header, 0);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 2u);                       // version
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), (uint)sectorsPerBand);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), 0u);                      // flags
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), (uint)maxLogicalBands);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), 1u);                      // next_physical_slot (1-based)
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), 0u);                      // allocated_count
    // header.AsSpan(28..) reserved + band-table — all zero (no logical band allocated).
    return header;
  }

  /// <summary>
  /// Writes <paramref name="bandData"/> into logical band
  /// <paramref name="logicalBandIndex"/>. If the band is already
  /// allocated, the existing physical slot is rewritten in place. If
  /// not, the next physical slot at the EOF is allocated, the
  /// band-table slot for <paramref name="logicalBandIndex"/> is updated,
  /// and the allocated-count + next-slot counters are bumped.
  /// </summary>
  /// <exception cref="ArgumentException">Container missing the
  /// <c>sprs</c> magic, or <paramref name="bandData"/> length differs
  /// from <c>sectors_per_band * 512</c>.</exception>
  public static void WriteBand(Stream image, int logicalBandIndex, ReadOnlySpan<byte> bandData) {
    ArgumentNullException.ThrowIfNull(image);
    if (logicalBandIndex < 0) throw new ArgumentOutOfRangeException(nameof(logicalBandIndex));

    var container = AppleSparseReader.TryRead(image)
      ?? throw new ArgumentException("Stream is not an Apple sparseimage container.", nameof(image));

    if (logicalBandIndex >= container.MaxLogicalBands)
      throw new ArgumentOutOfRangeException(nameof(logicalBandIndex),
        $"Logical band index {logicalBandIndex} exceeds container max_logical_bands ({container.MaxLogicalBands}).");

    if (bandData.Length != container.BandSize)
      throw new ArgumentException(
        $"Band data must be exactly {container.BandSize} bytes (sectors_per_band * {SectorBytes}); got {bandData.Length}.",
        nameof(bandData));

    var bandTableSlotOffset = HeaderPreambleSize + logicalBandIndex * 4;

    // Read the current band-table slot directly to decide allocate-vs-rewrite.
    image.Position = bandTableSlotOffset;
    Span<byte> slotBytes = stackalloc byte[4];
    var read = image.Read(slotBytes);
    if (read < 4)
      throw new InvalidDataException("Sparseimage header truncated; cannot read band-table slot.");
    var currentSlot = (int)BinaryPrimitives.ReadUInt32BigEndian(slotBytes);

    if (currentSlot != 0) {
      // In-place rewrite: known byte offset, no header mutation needed.
      var byteOffset = (long)HeaderSize + (long)(currentSlot - 1) * container.BandSize;
      image.Position = byteOffset;
      image.Write(bandData);
      return;
    }

    // Allocate fresh physical slot at EOF and stamp the band-table entry.
    var newSlot = container.NextPhysicalSlot;
    var newByteOffset = (long)HeaderSize + (long)(newSlot - 1) * container.BandSize;

    image.Position = newByteOffset;
    image.Write(bandData);

    BinaryPrimitives.WriteUInt32BigEndian(slotBytes, (uint)newSlot);
    image.Position = bandTableSlotOffset;
    image.Write(slotBytes);

    // Bump next_physical_slot and allocated_count.
    BinaryPrimitives.WriteUInt32BigEndian(slotBytes, (uint)(newSlot + 1));
    image.Position = 20;
    image.Write(slotBytes);

    BinaryPrimitives.WriteUInt32BigEndian(slotBytes, (uint)(container.AllocatedCount + 1));
    image.Position = 24;
    image.Write(slotBytes);
  }

  /// <summary>
  /// Drops logical band <paramref name="logicalBandIndex"/>: zeros the
  /// existing band's payload bytes at its known offset, clears the
  /// band-table slot, and decrements the allocated-count. The physical
  /// slot is retained (left as a zero-filled hole) so the LBA-to-offset
  /// map for every other allocated band stays byte-identical. Returns
  /// <c>true</c> if the band was allocated and removed; <c>false</c>
  /// otherwise.
  /// </summary>
  public static bool RemoveBand(Stream image, int logicalBandIndex) {
    ArgumentNullException.ThrowIfNull(image);
    if (logicalBandIndex < 0) return false;

    var container = AppleSparseReader.TryRead(image);
    if (container == null) return false;
    if (logicalBandIndex >= container.MaxLogicalBands) return false;

    var bandTableSlotOffset = HeaderPreambleSize + logicalBandIndex * 4;
    image.Position = bandTableSlotOffset;
    Span<byte> slotBytes = stackalloc byte[4];
    var read = image.Read(slotBytes);
    if (read < 4) return false;
    var currentSlot = (int)BinaryPrimitives.ReadUInt32BigEndian(slotBytes);
    if (currentSlot == 0) return false;

    // Wipe the band's payload bytes.
    var bandOffset = (long)HeaderSize + (long)(currentSlot - 1) * container.BandSize;
    if (bandOffset + container.BandSize <= image.Length) {
      var zeros = new byte[container.BandSize];
      image.Position = bandOffset;
      image.Write(zeros);
    }

    // Clear the band-table slot.
    BinaryPrimitives.WriteUInt32BigEndian(slotBytes, 0u);
    image.Position = bandTableSlotOffset;
    image.Write(slotBytes);

    // Decrement allocated_count.
    BinaryPrimitives.WriteUInt32BigEndian(slotBytes, (uint)Math.Max(0, container.AllocatedCount - 1));
    image.Position = 24;
    image.Write(slotBytes);
    return true;
  }

  // ── IArchiveModifiable bridges ──────────────────────────────────────

  /// <summary>
  /// Parses a synthetic <c>band-NNNN.bin</c> entry name and returns the
  /// embedded logical band index. Names that don't match the schema
  /// return <c>false</c> so callers can ignore them rather than throwing.
  /// </summary>
  public static bool TryParseBandEntryName(string entryName, out int logicalBandIndex) {
    logicalBandIndex = -1;
    if (string.IsNullOrEmpty(entryName)) return false;
    var leaf = Path.GetFileName(entryName);
    const string prefix = "band-";
    const string suffix = ".bin";
    if (!leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    if (!leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
    var numeric = leaf.AsSpan(prefix.Length, leaf.Length - prefix.Length - suffix.Length);
    return int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out logicalBandIndex)
           && logicalBandIndex >= 0;
  }

  /// <summary>
  /// Format the synthetic entry name used by the modifier. Four-digit
  /// zero-padded logical band index.
  /// </summary>
  public static string FormatBandEntryName(int logicalBandIndex)
    => AppleSparseReader.FormatBandName(logicalBandIndex);

  /// <summary>
  /// Routes each input through the band-rewrite path. Inputs whose
  /// <c>ArchiveName</c> matches <c>band-NNNN.bin</c> are written at the
  /// fixed logical-band byte offset (existing → rewrite, unallocated →
  /// allocate new physical slot + update band-table slot). Inputs not
  /// matching the schema are silently skipped.
  /// </summary>
  public static void AddOrReplaceBands(Stream image, IEnumerable<(string ArchiveName, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in inputs) {
      if (!TryParseBandEntryName(name, out var logical)) continue;
      WriteBand(image, logical, data);
    }
  }

  /// <summary>
  /// Zeros + frees the named logical bands. Names that don't match the
  /// schema are silently skipped; bands that aren't allocated are
  /// likewise no-ops.
  /// </summary>
  public static void RemoveBands(Stream image, IEnumerable<string> entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames) {
      if (!TryParseBandEntryName(name, out var logical)) continue;
      RemoveBand(image, logical);
    }
  }
}
