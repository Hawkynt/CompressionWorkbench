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
  public static byte[] Build(IReadOnlyList<(uint EntryId, byte[] Data)> entries)
    => Build(entries, AppleSingleReader.MagicSingle);

  /// <summary>
  /// Serializes the given entries under an explicit container magic —
  /// <see cref="AppleSingleReader.MagicSingle"/> or
  /// <see cref="AppleSingleReader.MagicDouble"/>.
  /// </summary>
  /// <remarks>
  /// RFC 1740 gives the two containers one body and two headers: the entry-id namespace, the
  /// 26-byte header, the 12-byte directory slots and the payload area are identical, and only the
  /// leading 32-bit magic distinguishes them. AppleDouble is the same file minus the data fork,
  /// which is why <see cref="AppleSingleReader.Read" /> has always accepted both.
  /// </remarks>
  public static byte[] Build(IReadOnlyList<(uint EntryId, byte[] Data)> entries, uint magic) {
    ArgumentNullException.ThrowIfNull(entries);
    if (entries.Count > ushort.MaxValue)
      throw new ArgumentException($"AppleSingle: too many entries ({entries.Count} > {ushort.MaxValue}).", nameof(entries));

    var dataStart = 26 + 12 * entries.Count;
    var total = dataStart;
    for (var i = 0; i < entries.Count; i++)
      total += entries[i].Data.Length;

    var buf = new byte[total];
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), magic);
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
  /// Human-readable summary of the names <see cref="TryEntryIdForName"/> accepts,
  /// for the write-constraint tooltips of both AppleSingle and AppleDouble.
  /// </summary>
  public const string AcceptedEntryNames =
    "Accepts data_fork.bin, resource_fork.bin, real_name.txt, comment.txt, icon_bw.bin, "
    + "icon_color.bin, file_dates.bin, finder_info.bin, macintosh_file_info.bin, "
    + "prodos_file_info.bin, msdos_file_info.bin, short_name.txt, afp_file_info.bin, "
    + "afp_directory_id.bin, afp_signature.bin, or entry_NNNNN.bin for any other entry id.";

  /// <summary>
  /// Maps a stable display name (the same one
  /// <see cref="AppleSingleReader.EntryName"/> emits) back to the AppleSingle
  /// entry id. Unknown names following the <c>entry_NNNNN.bin</c> shape
  /// recover their numeric id; anything else throws.
  /// </summary>
  public static uint EntryIdForName(string name)
    => TryEntryIdForName(name, out var id)
      ? id
      : throw new ArgumentException($"AppleSingle: cannot map name '{name}' to an entry id.", nameof(name));

  /// <summary>
  /// The non-throwing half of <see cref="EntryIdForName"/>, so a caller can ask
  /// whether a name belongs in the container without provoking an exception.
  /// </summary>
  public static bool TryEntryIdForName(string name, out uint entryId) {
    ArgumentNullException.ThrowIfNull(name);
    entryId = 0;
    switch (name) {
      case "data_fork.bin": entryId = 1; return true;
      case "resource_fork.bin": entryId = 2; return true;
      case "real_name.txt": entryId = 3; return true;
      case "comment.txt": entryId = 4; return true;
      case "icon_bw.bin": entryId = 5; return true;
      case "icon_color.bin": entryId = 6; return true;
      case "file_dates.bin": entryId = 7; return true;
      case "finder_info.bin": entryId = 8; return true;
      case "macintosh_file_info.bin": entryId = 9; return true;
      case "prodos_file_info.bin": entryId = 10; return true;
      case "msdos_file_info.bin": entryId = 11; return true;
      case "short_name.txt": entryId = 12; return true;
      case "afp_file_info.bin": entryId = 13; return true;
      case "afp_directory_id.bin": entryId = 14; return true;
      case "afp_signature.bin": entryId = 15; return true;
    }
    if (name.StartsWith("entry_", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal)) {
      var digits = name.Substring(6, name.Length - 6 - 4);
      if (uint.TryParse(digits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)) {
        entryId = id;
        return true;
      }
    }
    return false;
  }
}
