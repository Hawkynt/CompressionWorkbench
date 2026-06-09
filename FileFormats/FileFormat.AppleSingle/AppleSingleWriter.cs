#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.AppleSingle;

/// <summary>
/// WORM writer for AppleSingle (RFC 1740) container files. Produces a v2 container
/// whose 26-byte header is followed by an N×12 entry directory and contiguous
/// entry payloads. Each input becomes one entry: when the archive name maps to a
/// documented entry id (data_fork.bin, resource_fork.bin, real_name.txt, etc.)
/// that id is used; otherwise the input is stored under a high-range id
/// (0x80000000 + index) so reads still surface it.
/// </summary>
/// <remarks>
/// Layout (offsets are absolute):
/// <list type="bullet">
///   <item><c>0..3</c> — magic <c>0x00051600</c> (BE).</item>
///   <item><c>4..7</c> — version <c>0x00020000</c> (BE).</item>
///   <item><c>8..23</c> — 16-byte home-filesystem filler (zeroed).</item>
///   <item><c>24..25</c> — entry count <c>nEntries</c> (BE u16).</item>
///   <item><c>26..(26+12N)</c> — entry directory: each row <c>id(BE u32) + offset(BE u32) + length(BE u32)</c>.</item>
///   <item>Per-entry bodies, concatenated after the directory in directory order.</item>
/// </list>
/// </remarks>
public sealed class AppleSingleWriter {

  /// <summary>
  /// Writes an AppleSingle (or AppleDouble, when <paramref name="isDouble"/> is true) container
  /// to <paramref name="output"/>. The stream's current position is the start of the container.
  /// </summary>
  public static void Write(Stream output, IReadOnlyList<(string Name, byte[] Data)> inputs, bool isDouble = false) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    if (inputs.Count > ushort.MaxValue)
      throw new ArgumentException("AppleSingle: too many entries (max 65535).", nameof(inputs));

    var entries = new (uint Id, byte[] Data)[inputs.Count];
    for (var i = 0; i < inputs.Count; i++)
      entries[i] = (NameToEntryId(inputs[i].Name, i), inputs[i].Data);

    var headerLen = 26 + 12 * entries.Length;
    var totalLen = headerLen;
    foreach (var e in entries) totalLen += e.Data.Length;

    var buf = new byte[totalLen];
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4),
      isDouble ? AppleSingleReader.MagicDouble : AppleSingleReader.MagicSingle);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), 0x00020000);
    // 16-byte filler at 8..23 left zeroed.
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(24, 2), (ushort)entries.Length);

    var bodyCursor = headerLen;
    for (var i = 0; i < entries.Length; i++) {
      var off = 26 + 12 * i;
      var (id, data) = entries[i];
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off, 4), id);
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off + 4, 4), (uint)bodyCursor);
      BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(off + 8, 4), (uint)data.Length);
      data.CopyTo(buf.AsSpan(bodyCursor));
      bodyCursor += data.Length;
    }

    output.Write(buf, 0, buf.Length);
  }

  /// <summary>
  /// Maps a documented archive entry name back to its RFC 1740 entry id, or to a
  /// fallback "unknown" id derived from the index when the name doesn't match a
  /// well-known role. The fallback id places custom entries above the documented
  /// range so a reader's name lookup still works.
  /// </summary>
  public static uint NameToEntryId(string name, int fallbackIndex) {
    var leaf = name;
    var slash = name.LastIndexOfAny(['/', '\\']);
    if (slash >= 0) leaf = name[(slash + 1)..];
    return leaf switch {
      "data_fork.bin" => 1,
      "resource_fork.bin" => 2,
      "real_name.txt" => 3,
      "comment.txt" => 4,
      "icon_bw.bin" => 5,
      "icon_color.bin" => 6,
      "file_dates.bin" => 7,
      "finder_info.bin" => 8,
      "macintosh_file_info.bin" => 9,
      "prodos_file_info.bin" => 10,
      "msdos_file_info.bin" => 11,
      "short_name.txt" => 12,
      "afp_file_info.bin" => 13,
      "afp_directory_id.bin" => 14,
      "afp_signature.bin" => 15,
      _ => 0x80000000u + (uint)fallbackIndex,
    };
  }
}
