#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.AppleSingle;

/// <summary>
/// Writer for Apple's AppleSingle (RFC 1740) container format. Emits the
/// canonical 26-byte header followed by an <c>N×12-byte</c> entry directory
/// and the per-entry payloads at contiguous offsets behind the directory.
/// </summary>
/// <remarks>
/// <para>Each input (name → bytes) is mapped to an entry id by reversing the
/// well-known names produced by <see cref="AppleSingleReader.EntryName"/>.
/// Unknown names that follow the <c>entry_NNNNN.bin</c> shape recover the
/// numeric id; anything else is rejected so unrelated filenames cannot
/// silently corrupt the entry table.</para>
/// </remarks>
public static class AppleSingleWriter {

  /// <summary>
  /// Serializes the given entries into a single AppleSingle byte buffer.
  /// Entries appear in caller-supplied order, both in the directory and in
  /// the data area immediately after it. The 16-byte filler block is left
  /// zero (RFC 1740 v2 convention).
  /// </summary>
  public static byte[] Build(IReadOnlyList<(uint EntryId, byte[] Data)> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    if (entries.Count > ushort.MaxValue)
      throw new ArgumentException($"AppleSingle: too many entries ({entries.Count} > {ushort.MaxValue}).", nameof(entries));

    var dataStart = 26 + 12 * entries.Count;
    var total = dataStart;
    for (var i = 0; i < entries.Count; i++)
      total += entries[i].Data.Length;

    var buf = new byte[total];
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), AppleSingleReader.MagicSingle);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), 0x00020000); // version 2
    // Bytes 8..24 = 16-byte zero filler (RFC 1740 v2).
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(24), (ushort)entries.Count);

    var cursor = dataStart;
    for (var i = 0; i < entries.Count; i++) {
      var off = 26 + 12 * i;
      var (id, data) = entries[i];
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off), id);
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off + 4), (uint)cursor);
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off + 8), (uint)data.Length);
      data.CopyTo(buf.AsSpan(cursor));
      cursor += data.Length;
    }

    return buf;
  }

  /// <summary>
  /// Maps a stable display name (the same one
  /// <see cref="AppleSingleReader.EntryName"/> emits) back to the AppleSingle
  /// entry id. Unknown names following the <c>entry_NNNNN.bin</c> shape
  /// recover their numeric id; anything else throws.
  /// </summary>
  public static uint EntryIdForName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    switch (name) {
      case "data_fork.bin": return 1;
      case "resource_fork.bin": return 2;
      case "real_name.txt": return 3;
      case "comment.txt": return 4;
      case "icon_bw.bin": return 5;
      case "icon_color.bin": return 6;
      case "file_dates.bin": return 7;
      case "finder_info.bin": return 8;
      case "macintosh_file_info.bin": return 9;
      case "prodos_file_info.bin": return 10;
      case "msdos_file_info.bin": return 11;
      case "short_name.txt": return 12;
      case "afp_file_info.bin": return 13;
      case "afp_directory_id.bin": return 14;
      case "afp_signature.bin": return 15;
    }
    if (name.StartsWith("entry_", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal)) {
      var digits = name.Substring(6, name.Length - 6 - 4);
      if (uint.TryParse(digits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
        return id;
    }
    throw new ArgumentException($"AppleSingle: cannot map name '{name}' to an entry id.", nameof(name));
  }
}
