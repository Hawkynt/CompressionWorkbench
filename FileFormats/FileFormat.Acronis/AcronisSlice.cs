#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Acronis;

/// <summary>Form of an Acronis slice — determines how the trailer is laid out.</summary>
public enum AcronisSliceForm {
  /// <summary>
  /// Specifies an unknown or unrecognized value.
  /// </summary>
  Unknown,
  /// <summary>File/directory-based backup (per-file index records). Trailer magic <c>2C 8A E1 94</c>.</summary>
  FileSystem,
  /// <summary>Sector-by-sector backup. Trailer magic <c>2B 8A E1 94</c>. Metadata offset is variable-length encoded.</summary>
  SectorBySector,
}

/// <summary>
/// Trailer-parser for a single Acronis classic .tib slice (final volume of a slice).
/// </summary>
/// <remarks>
/// <para>
/// Per upstream RE (src/win/volume.ts and src/win/slice.ts), the last volume of a slice ends with:
/// </para>
/// <list type="bullet">
///   <item><description>48-byte footer: 8-byte uncompressed slice size (uint64 LE) + 8 reserved bytes + 32-byte
///   mirror of the volume header in reverse byte order. The mirror is what we validate.</description></item>
///   <item><description>Immediately before the footer, the last 4 bytes of the trailer payload distinguish slice form:
///   <c>2C 8A E1 94</c> for file-system slices, <c>2B 8A E1 94</c> for sector slices.</description></item>
///   <item><description>For file-system slices, the metadata-offset (absolute archive position of the first
///   metadata record in the slice) is a uint64 LE located 12 bytes before the trailer end.</description></item>
/// </list>
/// </remarks>
public sealed record AcronisSliceTrailer(
  bool MirrorValid,
  AcronisSliceForm Form,
  long MetadataOffset,
  long SliceSize
) {

  private static readonly byte[] MagicFs = [0x2C, 0x8A, 0xE1, 0x94];
  private static readonly byte[] MagicSec = [0x2B, 0x8A, 0xE1, 0x94];

  /// <summary>
  /// Reads the trailer from the end of the slice. The header is needed to validate the mirror image.
  /// Returns <c>null</c> when the file is too small for a valid trailer (e.g. multi-volume slice
  /// where only an intermediate volume was opened).
  /// </summary>
  public static AcronisSliceTrailer? TryRead(Stream stream, AcronisVolumeHeader header) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(header);
    var size = stream.Length;
    // 32-byte header + 48-byte footer + trailer payload (at least one form-magic word = 4 bytes) = 84 minimum.
    if (size < 90) return null;

    var footer = new byte[48];
    stream.Position = size - 48;
    stream.ReadExactly(footer);

    // The footer's last 32 bytes are the volume header byte-reversed. Re-read the original 32-byte
    // header from disk and compare against footer[16..48] reversed.
    Span<byte> rawHeader = stackalloc byte[32];
    stream.Position = 0;
    stream.ReadExactly(rawHeader);

    var mirror = footer.AsSpan(16, 32);
    var mirrorValid = true;
    for (var i = 0; i < 32; i++) {
      if (rawHeader[i] != mirror[31 - i]) { mirrorValid = false; break; }
    }

    var sliceSize = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(0, 8));

    // Now hunt for the slice-form marker. It sits immediately before the 48-byte footer; for
    // file-system slices the trailer payload is 12 bytes (uint64 metaOffset + 4-byte form magic),
    // so we read 12 bytes ending at (size - 48).
    if (size < 48 + 12) return new AcronisSliceTrailer(mirrorValid, AcronisSliceForm.Unknown, -1, sliceSize);

    var trailer = new byte[12];
    stream.Position = size - 48 - 12;
    stream.ReadExactly(trailer);

    var magicSpan = trailer.AsSpan(8, 4);
    AcronisSliceForm form;
    if (magicSpan.SequenceEqual(MagicFs)) form = AcronisSliceForm.FileSystem;
    else if (magicSpan.SequenceEqual(MagicSec)) form = AcronisSliceForm.SectorBySector;
    else return new AcronisSliceTrailer(mirrorValid, AcronisSliceForm.Unknown, -1, sliceSize);

    if (form == AcronisSliceForm.FileSystem) {
      // uint64 LE metadata offset at trailer[0..8] = absolute archive offset where records start.
      var metaOffset = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(0, 8));
      return new AcronisSliceTrailer(mirrorValid, form, metaOffset, sliceSize);
    }

    // Sector-by-sector trailer is variable-length and not parsed here (upstream marks it
    // "mode not yet supported"). Return what we have.
    return new AcronisSliceTrailer(mirrorValid, form, -1, sliceSize);
  }
}
