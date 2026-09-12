#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.FontCollection;

/// <summary>
/// WORM writer for TrueType / OpenType font collections (.ttc / .otc). Bundles
/// one or more standalone SFNT inputs (.ttf or .otf) into a single TTC v1
/// container per Microsoft OpenType / Apple TTC documentation.
/// </summary>
/// <remarks>
/// <para>
/// Layout per the OpenType TTC Header (TTC v1):
/// <list type="bullet">
///   <item><c>ttcTag</c> ("ttcf", 4 bytes) — collection magic.</item>
///   <item><c>majorVersion</c>/<c>minorVersion</c> — 1/0 for TTC v1 (no DSIG).</item>
///   <item><c>numFonts</c> (BE u32) — member count.</item>
///   <item><c>offsetTable</c> — N×u32 absolute offsets to each member's Offset Subtable.</item>
/// </list>
/// </para>
/// <para>
/// Each member font is copied byte-for-byte into the output and its absolute
/// offset is recorded in the table. The writer does <b>not</b> attempt to share
/// tables across members; that optimisation requires per-table checksum and
/// content matching and is not part of the format requirement — the OpenType
/// spec explicitly allows TTCs whose members fully duplicate their tables.
/// </para>
/// <para>
/// Inputs must be valid standalone SFNT fonts: the first 4 bytes must be one
/// of <c>0x00010000</c> (TrueType), <c>OTTO</c> (CFF), <c>true</c>, or
/// <c>typ1</c>. The writer rejects anything else so it cannot produce a TTC
/// whose offset table points at non-SFNT bytes.
/// </para>
/// </remarks>
public sealed class TtcWriter {

  private static readonly byte[] TtcfMagic = "ttcf"u8.ToArray();

  /// <summary>
  /// Writes a TTC v1 container bundling <paramref name="fonts"/> to
  /// <paramref name="output"/>. Each member font's bytes are copied verbatim
  /// from the input, after a header containing absolute offsets into the
  /// concatenated payload.
  /// </summary>
  public static void Write(Stream output, IReadOnlyList<byte[]> fonts) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(fonts);
    if (fonts.Count == 0)
      throw new ArgumentException("TTC: at least one member font is required.", nameof(fonts));

    foreach (var (font, idx) in fonts.Select((f, i) => (f, i)))
      if (!IsStandaloneSfnt(font))
        throw new ArgumentException(
          $"TTC: input #{idx} is not a standalone SFNT font (expected sfnt version 0x00010000 / OTTO / true / typ1).",
          nameof(fonts));

    var headerSize = 12 + 4 * fonts.Count;
    var totalSize = headerSize;
    foreach (var f in fonts) totalSize += f.Length;

    var buf = new byte[totalSize];
    TtcfMagic.CopyTo(buf.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), 1);  // majorVersion
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), 0);  // minorVersion
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), (uint)fonts.Count);

    var cursor = headerSize;
    for (var i = 0; i < fonts.Count; i++) {
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12 + 4 * i, 4), (uint)cursor);
      fonts[i].CopyTo(buf.AsSpan(cursor));
      cursor += fonts[i].Length;
    }

    output.Write(buf, 0, buf.Length);
  }

  /// <summary>
  /// Returns true if <paramref name="data"/>'s first 4 bytes match one of the
  /// recognised standalone SFNT versions. Rejects anything else so the writer
  /// can't produce a TTC whose member offsets point at non-font bytes.
  /// </summary>
  internal static bool IsStandaloneSfnt(byte[] data) {
    if (data.Length < 4) return false;
    var v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
    return v switch {
      0x00010000u => true,            // TrueType outlines
      0x4F54544Fu => true,            // 'OTTO' — CFF outlines
      0x74727565u => true,            // 'true' — legacy Mac TrueType
      0x74797031u => true,            // 'typ1' — PostScript Type 1 in SFNT
      _ => false,
    };
  }
}
