#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Gettext;

/// <summary>
/// WORM writer for GNU gettext .mo binary catalogs (little-endian, revision 0).
/// Layout follows
/// https://www.gnu.org/software/gettext/manual/html_node/MO-Files.html:
/// 28-byte header, then two parallel descriptor tables (orig + translation),
/// then the two NUL-terminated string pools.
/// </summary>
/// <remarks>
/// Entries with an empty msgid (the metadata HEADER) are placed first as
/// gettext requires. Within other entries, the input order is preserved
/// rather than sorted by msgid — gettext recommends sorted order for
/// binary-search lookup at runtime, but it's not a format requirement and
/// keeping input order makes WORM-then-read round-trips reproducible. The
/// runtime cost is a linear scan instead of a binary search — fine for the
/// 1–N entry sizes typical of tests/fixtures.
/// </remarks>
public sealed class MoWriter {

  /// <summary>Magic for little-endian MO files (per the gettext spec).</summary>
  public const uint MagicLe = 0x950412DEu;

  /// <summary>
  /// Writes an MO catalog containing <paramref name="entries"/>. Each entry's
  /// msgid + msgstr are written as UTF-8 NUL-terminated strings. Plural forms
  /// (msgid_plural / msgstr[N]) are encoded with NUL separators per the spec.
  /// Context (msgctxt) is prefixed onto the msgid with the EOT (U+0004)
  /// separator.
  /// </summary>
  public static void Write(Stream output, IReadOnlyList<CatalogEntry> entries) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(entries);

    // Reorder so the empty-msgid header (if present) is first.
    var ordered = entries
      .OrderBy(e => string.IsNullOrEmpty(e.MsgId) ? 0 : 1)
      .ThenBy(e => e.Index)
      .ToList();

    var origs = new byte[ordered.Count][];
    var trans = new byte[ordered.Count][];
    for (var i = 0; i < ordered.Count; i++) {
      origs[i] = EncodeOriginal(ordered[i]);
      trans[i] = EncodeTranslation(ordered[i]);
    }

    const int HeaderSize = 28;
    var origTableOffset = HeaderSize;
    var transTableOffset = origTableOffset + 8 * ordered.Count;
    var origPoolOffset = transTableOffset + 8 * ordered.Count;

    // Compute per-string offsets within the original-strings pool.
    var origOffsets = new uint[ordered.Count];
    var pos = origPoolOffset;
    for (var i = 0; i < ordered.Count; i++) {
      origOffsets[i] = (uint)pos;
      pos += origs[i].Length + 1; // NUL terminator
    }
    var transPoolOffset = pos;
    var transOffsets = new uint[ordered.Count];
    for (var i = 0; i < ordered.Count; i++) {
      transOffsets[i] = (uint)pos;
      pos += trans[i].Length + 1;
    }
    var total = pos;

    var buf = new byte[total];

    // Header.
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), MagicLe);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 0);                          // file format revision
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), (uint)ordered.Count);        // nstrings
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), (uint)origTableOffset);     // offset orig table
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), (uint)transTableOffset);    // offset trans table
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), 0);                         // hash table size = 0 (no hash)
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), 0);                         // hash table offset = 0

    // Descriptor tables.
    for (var i = 0; i < ordered.Count; i++) {
      var o = origTableOffset + 8 * i;
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o, 4), (uint)origs[i].Length);
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 4, 4), origOffsets[i]);
      var t = transTableOffset + 8 * i;
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(t, 4), (uint)trans[i].Length);
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(t + 4, 4), transOffsets[i]);
    }

    // String pools (each entry NUL-terminated).
    for (var i = 0; i < ordered.Count; i++) {
      origs[i].CopyTo(buf.AsSpan((int)origOffsets[i]));
      // NUL terminator already zero-initialised.
      trans[i].CopyTo(buf.AsSpan((int)transOffsets[i]));
    }

    output.Write(buf, 0, buf.Length);
  }

  private static byte[] EncodeOriginal(CatalogEntry e) {
    var msgid = e.MsgIdPlural != null ? e.MsgId + "\0" + e.MsgIdPlural : e.MsgId;
    if (!string.IsNullOrEmpty(e.Context))
      msgid = e.Context + "" + msgid;
    return Encoding.UTF8.GetBytes(msgid);
  }

  private static byte[] EncodeTranslation(CatalogEntry e) {
    if (e.MsgStrPlural != null)
      return Encoding.UTF8.GetBytes(string.Join("\0", e.MsgStrPlural));
    return Encoding.UTF8.GetBytes(e.MsgStr);
  }
}
