#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.AppleSingle;

/// <summary>
/// Reader for Apple's AppleSingle and AppleDouble container formats (RFC 1740).
/// AppleSingle bundles data fork, resource fork, and Finder metadata into one
/// file; AppleDouble splits the data fork off and stores everything else in a
/// sibling file (the <c>._foo</c> companions Macs leave on non-HFS filesystems).
/// Both share an identical entry-table layout.
/// </summary>
public sealed class AppleSingleReader {

  public const uint MagicSingle = 0x00051600;
  public const uint MagicDouble = 0x00051607;

  public sealed record Entry(uint EntryId, string Name, byte[] Data);

  public sealed record Container(
    bool IsDouble,
    uint Version,
    IReadOnlyList<Entry> Entries);

  public static Container Read(ReadOnlySpan<byte> data) {
    if (data.Length < 26) throw new InvalidDataException("AppleSingle: file shorter than 26-byte header.");

    var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
    bool isDouble;
    if (magic == MagicSingle) isDouble = false;
    else if (magic == MagicDouble) isDouble = true;
    else throw new InvalidDataException($"AppleSingle: bad magic 0x{magic:X8}");

    var version = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
    // bytes 8..24: 16-byte filler (originally home filesystem identifier; ignored in v2).

    var numEntries = BinaryPrimitives.ReadUInt16BigEndian(data[24..]);
    var headerEnd = 26 + 12 * numEntries;
    if (headerEnd > data.Length)
      throw new InvalidDataException($"AppleSingle: entry table extends past end of file ({headerEnd} > {data.Length})");

    var entries = new List<Entry>(numEntries);
    for (var i = 0; i < numEntries; i++) {
      var off = 26 + 12 * i;
      var id = BinaryPrimitives.ReadUInt32BigEndian(data[off..]);
      var dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(off + 4)..]);
      var dataLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(off + 8)..]);
      if ((long)dataOffset + dataLen > data.Length) continue; // skip malformed entry

      var body = data.Slice(dataOffset, dataLen).ToArray();
      entries.Add(new Entry(id, EntryName(id), body));
    }

    return new Container(isDouble, version, entries);
  }

  /// <summary>Maps an AppleSingle/AppleDouble entry id to a stable display name.</summary>
  public static string EntryName(uint id) => id switch {
    1 => "data_fork.bin",
    2 => "resource_fork.bin",
    3 => "real_name.txt",
    4 => "comment.txt",
    5 => "icon_bw.bin",
    6 => "icon_color.bin",
    7 => "file_dates.bin",
    8 => "finder_info.bin",
    9 => "macintosh_file_info.bin",
    10 => "prodos_file_info.bin",
    11 => "msdos_file_info.bin",
    12 => "short_name.txt",
    13 => "afp_file_info.bin",
    14 => "afp_directory_id.bin",
    15 => "afp_signature.bin",
    _ => $"entry_{id:D5}.bin",
  };

  /// <summary>Returns the human-readable entry text when the entry id is documented.</summary>
  public static string EntryDescription(uint id) => id switch {
    1 => "Data Fork",
    2 => "Resource Fork",
    3 => "Real Name",
    4 => "Comment",
    5 => "B/W Icon (1-bit)",
    6 => "Color Icon",
    7 => "File Dates Info",
    8 => "Finder Info",
    9 => "Macintosh File Info",
    10 => "ProDOS File Info",
    11 => "MS-DOS File Info",
    12 => "Short Name (8.3 DOS)",
    13 => "AFP File Info",
    14 => "AFP Directory ID",
    15 => "AFP Signature",
    _ => $"Unknown ({id})",
  };

  /// <summary>Decodes the embedded "real_name" entry as a UTF-8 (or MacRoman ASCII) string.</summary>
  public static string DecodeRealName(byte[] data) =>
    Encoding.UTF8.GetString(data).TrimEnd('\0', ' ');
}
