#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;

namespace FileFormat.AppleSparse;

/// <summary>
/// In-place band-rewrite modifier for an Apple sparseimage. Operates at the
/// physical band offsets that the <see cref="SparseimageReader"/> already
/// derives from the on-disk header: a 4 096-byte header preamble at offset 0,
/// a band-allocation table (BAT) of <c>num_bands × 4</c> big-endian entries
/// starting at offset <see cref="SparseimageReader.HeaderSize"/>, and the
/// first physical band's data at the first 512-byte-aligned offset past the
/// BAT.
///
/// <para><b>Scope.</b> The modifier rewrites the
/// <c>band_size</c>-byte payload of an existing or freshly-allocated physical
/// band slot, and toggles the BAT entry for the matching logical band. It
/// does <i>not</i> understand the inner HFS+/APFS/FAT directory structure
/// of the virtual disk — that is delegated to the respective filesystem
/// descriptors. Synthetic entry names of the form <c>band-NNNN.bin</c>
/// address a single logical band directly; inputs whose name doesn't match
/// the schema are silently skipped at the descriptor seam.</para>
///
/// <para><b>True in-place.</b> Writes touch only the targeted band's
/// physical payload window plus the 4-byte BAT entry for the matching
/// logical band. The 4 096-byte header preamble, every other BAT entry,
/// every other allocated band's payload, and the trailing physical-band
/// region of the image stay byte-identical at their original byte offsets.
/// New-band allocation lands at the current end-of-stream and grows the
/// image by exactly one band; existing payload offsets are preserved.</para>
///
/// <para><b>Honest-scope.</b> The BAT length <c>num_bands</c> is treated as
/// fixed: writing to a logical band whose index is past <c>num_bands - 1</c>
/// is rejected, because growing the BAT would shift the first-band offset
/// and break every other band's physical position. Inner virtual-disk
/// filesystems remain the writers' responsibility — only the raw band
/// surface is mutated here.</para>
/// </summary>
public static class SparseimageInPlaceModifier {

  /// <summary>
  /// Header geometry parsed once from the sparseimage stream so a caller
  /// rewriting several bands back-to-back can reuse the offsets instead of
  /// re-probing the header for each call.
  /// </summary>
  public readonly record struct BandGeometry(
    int SectorsPerBand,
    int BandSize,
    int NumBands,
    long BatOffset,
    long FirstBandOffset);

  /// <summary>
  /// Probes the 4 096-byte sparseimage header at offset 0 and returns the
  /// band-table geometry. Throws <see cref="InvalidDataException"/> on bad
  /// magic, unsupported header_size, implausible sectors_per_band, or
  /// implausible num_bands.
  /// </summary>
  public static BandGeometry ReadGeometry(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(image));
    if (image.Length < SparseimageReader.HeaderSize)
      throw new InvalidDataException("sparseimage: file too small (header is 4096 bytes).");

    image.Position = 0;
    Span<byte> hdr = stackalloc byte[SparseimageReader.HeaderSize];
    image.ReadExactly(hdr);

    if (!hdr[..4].SequenceEqual(SparseimageReader.Magic))
      throw new InvalidDataException("sparseimage: invalid magic (expected 'sprs').");

    var sectorsPerBand = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr[8..]);
    if (sectorsPerBand <= 0 || sectorsPerBand > 0x100000)
      throw new InvalidDataException($"sparseimage: implausible sectors_per_band {sectorsPerBand}.");

    var numBands = BinaryPrimitives.ReadUInt32BigEndian(hdr[24..]);
    if (numBands > 0x10000000)
      throw new InvalidDataException($"sparseimage: implausible num_bands {numBands}.");

    var headerSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[28..]);
    if (headerSize != SparseimageReader.HeaderSize)
      throw new InvalidDataException(
        $"sparseimage: unexpected header_size {headerSize} (this modifier supports the single-header variant only).");

    var bandSize = sectorsPerBand * SparseimageReader.SectorSize;
    var batOffset = (long)SparseimageReader.HeaderSize;
    var batBytes = (long)numBands * 4;
    var firstBandOffset = (batOffset + batBytes + SparseimageReader.SectorSize - 1)
                          & ~(long)(SparseimageReader.SectorSize - 1);

    return new BandGeometry(sectorsPerBand, bandSize, (int)numBands, batOffset, firstBandOffset);
  }

  /// <summary>
  /// Reads the BAT entry for <paramref name="logicalBand"/>. Returns 0 when
  /// the band is unallocated, otherwise the 1-based physical slot index.
  /// </summary>
  public static uint ReadBatEntry(Stream image, int logicalBand, BandGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (logicalBand < 0 || logicalBand >= geom.NumBands)
      throw new ArgumentOutOfRangeException(nameof(logicalBand));
    image.Position = geom.BatOffset + (long)logicalBand * 4;
    Span<byte> buf = stackalloc byte[4];
    image.ReadExactly(buf);
    return BinaryPrimitives.ReadUInt32BigEndian(buf);
  }

  /// <summary>Writes <paramref name="slot"/> (0 = unallocated, else 1-based
  /// physical slot index) to the BAT entry for <paramref name="logicalBand"/>.</summary>
  public static void WriteBatEntry(Stream image, int logicalBand, uint slot, BandGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (logicalBand < 0 || logicalBand >= geom.NumBands)
      throw new ArgumentOutOfRangeException(nameof(logicalBand));
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buf, slot);
    image.Position = geom.BatOffset + (long)logicalBand * 4;
    image.Write(buf);
  }

  /// <summary>
  /// Returns the highest 1-based physical slot referenced anywhere in the
  /// BAT — equivalently, the count of currently-allocated physical slots
  /// (since the writer packs slots sequentially with no holes). Returns 0
  /// when no band is allocated.
  /// </summary>
  public static uint MaxAllocatedSlot(Stream image, BandGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (geom.NumBands == 0) return 0;
    image.Position = geom.BatOffset;
    var buf = new byte[(long)geom.NumBands * 4];
    image.ReadExactly(buf);
    uint max = 0;
    for (var i = 0; i < geom.NumBands; i++) {
      var v = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i * 4, 4));
      if (v > max) max = v;
    }
    return max;
  }

  /// <summary>
  /// Rewrites the <c>band_size</c>-byte payload of logical band
  /// <paramref name="logicalBand"/> in place. When the band is already
  /// allocated, the data lands at its existing physical offset
  /// (<see cref="BandGeometry.FirstBandOffset"/> + (slot - 1) * band_size)
  /// and no other byte of the image is touched. When the band is not yet
  /// allocated, a fresh physical slot is appended at end-of-stream and the
  /// BAT entry for the logical band is updated to point at it; every
  /// previously-allocated band's payload stays byte-identical at its
  /// original offset.
  /// </summary>
  /// <exception cref="ArgumentException">When
  /// <paramref name="data"/>.Length differs from
  /// <see cref="BandGeometry.BandSize"/>.</exception>
  /// <exception cref="ArgumentOutOfRangeException">When
  /// <paramref name="logicalBand"/> is past
  /// <see cref="BandGeometry.NumBands"/> - growing the BAT would shift the
  /// first-band offset and is out of scope for the in-place modifier.</exception>
  public static void WriteBand(Stream image, int logicalBand, ReadOnlySpan<byte> data) {
    ArgumentNullException.ThrowIfNull(image);
    var geom = ReadGeometry(image);
    WriteBand(image, logicalBand, data, geom);
  }

  /// <summary>
  /// Variant of <see cref="WriteBand(Stream,int,ReadOnlySpan{byte})"/> that
  /// reuses a previously-probed geometry, avoiding a redundant header read
  /// per call when a caller is rewriting several bands back-to-back.
  /// </summary>
  public static void WriteBand(Stream image, int logicalBand, ReadOnlySpan<byte> data, BandGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (logicalBand < 0 || logicalBand >= geom.NumBands)
      throw new ArgumentOutOfRangeException(nameof(logicalBand),
        $"Logical band {logicalBand} is past the BAT (num_bands={geom.NumBands}); growing the BAT is out of scope for the in-place modifier.");
    if (data.Length != geom.BandSize)
      throw new ArgumentException(
        $"Band payload must be exactly {geom.BandSize} bytes; got {data.Length}.",
        nameof(data));

    var existing = ReadBatEntry(image, logicalBand, geom);
    long physOffset;
    if (existing != 0) {
      physOffset = geom.FirstBandOffset + (long)(existing - 1) * geom.BandSize;
    } else {
      // Allocate a fresh physical slot at end-of-stream so previously-allocated
      // bands' offsets stay byte-identical.
      var maxSlot = MaxAllocatedSlot(image, geom);
      var newSlot = maxSlot + 1;
      physOffset = geom.FirstBandOffset + (long)(newSlot - 1) * geom.BandSize;

      // Pad with zero-filled hole sectors if the new slot lands past EOF with
      // a gap (shouldn't happen for sequentially-packed images, but defend).
      if (physOffset > image.Length) {
        image.Position = image.Length;
        var pad = new byte[Math.Min(geom.BandSize, physOffset - image.Length)];
        var remaining = physOffset - image.Length;
        while (remaining > 0) {
          var step = (int)Math.Min(pad.Length, remaining);
          image.Write(pad, 0, step);
          remaining -= step;
        }
      }

      WriteBatEntry(image, logicalBand, newSlot, geom);
    }

    image.Position = physOffset;
    image.Write(data);
  }

  /// <summary>
  /// Zeros the physical payload of logical band <paramref name="logicalBand"/>
  /// and clears its BAT entry. The physical slot is left in place as a
  /// zero-filled hole so other bands' offsets don't shift; that matches the
  /// semantic of an unallocated band in the existing reader, which already
  /// returns zero bytes for BAT entries that are 0. Returns <c>true</c> if
  /// the band was previously allocated (and was zeroed), <c>false</c> when
  /// it was already unallocated.
  /// </summary>
  public static bool RemoveBand(Stream image, int logicalBand) {
    ArgumentNullException.ThrowIfNull(image);
    var geom = ReadGeometry(image);
    return RemoveBand(image, logicalBand, geom);
  }

  /// <summary>
  /// Variant of <see cref="RemoveBand(Stream,int)"/> reusing a previously-probed
  /// geometry.
  /// </summary>
  public static bool RemoveBand(Stream image, int logicalBand, BandGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (logicalBand < 0 || logicalBand >= geom.NumBands) return false;
    var existing = ReadBatEntry(image, logicalBand, geom);
    if (existing == 0) return false;

    var physOffset = geom.FirstBandOffset + (long)(existing - 1) * geom.BandSize;
    if (physOffset + geom.BandSize > image.Length) {
      // Truncated band — wipe whatever's actually on disk and still clear the BAT entry.
      var available = (int)Math.Max(0, image.Length - physOffset);
      if (available > 0) {
        var zeros = new byte[available];
        image.Position = physOffset;
        image.Write(zeros);
      }
    } else {
      var zeros = new byte[geom.BandSize];
      image.Position = physOffset;
      image.Write(zeros);
    }

    WriteBatEntry(image, logicalBand, 0u, geom);
    return true;
  }

  // ── IArchiveModifiable bridges ──────────────────────────────────────

  /// <summary>
  /// Parses a synthetic <c>band-NNNN.bin</c> entry name and returns the
  /// embedded logical band index. Names that don't match the schema return
  /// <c>false</c> so the descriptor can silently skip them rather than
  /// throwing.
  /// </summary>
  public static bool TryParseBandEntryName(string entryName, out int logicalBand) {
    logicalBand = -1;
    if (string.IsNullOrEmpty(entryName)) return false;
    var leaf = Path.GetFileName(entryName);
    const string prefix = "band-";
    const string suffix = ".bin";
    if (!leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    if (!leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
    var numeric = leaf.AsSpan(prefix.Length, leaf.Length - prefix.Length - suffix.Length);
    return int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out logicalBand)
           && logicalBand >= 0;
  }

  /// <summary>
  /// Formats a logical band index into the synthetic entry name used by the
  /// in-place modifier. Six-digit zero-padded so 0..999 999 sort
  /// lexicographically the same as numerically.
  /// </summary>
  public static string FormatBandEntryName(int logicalBand)
    => string.Create(CultureInfo.InvariantCulture, $"band-{logicalBand:D6}.bin");

  /// <summary>
  /// Routes each input through the band-rewrite path. Inputs whose
  /// <c>ArchiveName</c> matches <c>band-NNNN.bin</c> and carry exactly
  /// <see cref="BandGeometry.BandSize"/> bytes are written at the
  /// known physical band offset (in-place rewrite when allocated, fresh
  /// EOF slot otherwise). Inputs whose name doesn't match the schema or
  /// whose payload size doesn't match the band size are silently skipped
  /// — inner virtual-disk filesystem mutation is delegated to those
  /// filesystems' descriptors.
  /// </summary>
  public static void AddOrReplaceBands(Stream image, IEnumerable<(string ArchiveName, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    var geom = ReadGeometry(image);
    foreach (var (name, data) in inputs) {
      if (!TryParseBandEntryName(name, out var logicalBand))
        throw new NotSupportedException(
          $"Sparseimage: '{name}' cannot be added. A sparse image is edited a band at a "
          + "time, so an entry has to be named for the band it covers. Adding a file to the "
          + "filesystem inside the image is not something this supports.");
      // A band past the end, or one the wrong size, is as unwritable as a name
      // that names no band — and passing over it reports a write that did not
      // happen just the same.
      if (logicalBand >= geom.NumBands)
        throw new ArgumentOutOfRangeException(nameof(inputs),
          $"Sparseimage: '{name}' names band {logicalBand}, and the image has {geom.NumBands}.");
      if (data.Length != geom.BandSize)
        throw new ArgumentException(
          $"Sparseimage: '{name}' must carry exactly {geom.BandSize} bytes, one whole band; "
          + $"got {data.Length}.", nameof(inputs));
      WriteBand(image, logicalBand, data, geom);
    }
  }

  /// <summary>
  /// Zeros + clears the BAT entry for each named <c>band-NNNN.bin</c>.
  /// Names that don't match the schema, indices past the BAT, and bands
  /// that are already unallocated are silently skipped.
  /// </summary>
  public static void RemoveBands(Stream image, IEnumerable<string> entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    var geom = ReadGeometry(image);
    foreach (var name in entryNames) {
      if (!TryParseBandEntryName(name, out var logicalBand))
        throw new NotSupportedException(
          $"Sparseimage: '{name}' cannot be removed. A sparse image is edited a band at a "
          + "time, so an entry has to be named for the band it clears. Removing a file from the "
          + "filesystem inside the image is not something this supports.");
      RemoveBand(image, logicalBand, geom);
    }
  }
}
