#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Sid;

/// <summary>
/// Surfaces a C64 PSID/RSID tune as a read-only pseudo-archive: the verbatim file,
/// parsed (big-endian) header metadata — name, author, copyright, song count,
/// version and the SID chip model decoded from the v2+ flags — plus the raw C64
/// program data blob. It never emulates the 6502 or the SID chip and never throws
/// from listing.
/// </summary>
public static class SidDecomposer {

    /// <summary>
  /// Represents an entry kinds.
  /// </summary>
public static class EntryKinds {
        /// <summary>
    /// Defines the track constant value.
    /// </summary>
public const string Track = "Track";
        /// <summary>
    /// Defines the tag constant value.
    /// </summary>
public const string Tag = "Tag";
  }

    /// <summary>
  /// Represents an entry.
  /// </summary>
public readonly record struct Entry(string Name, byte[] Data, string Kind);

    /// <summary>
  /// Performs the decompose operation.
  /// </summary>
public static List<Entry> Decompose(byte[] file) {
    var entries = new List<Entry> { new("FULL.sid", file, EntryKinds.Track) };
    var meta = new IniBuilder("sid");
    var ok = false;
    var program = (byte[]?)null;

    try {
      if (file.Length >= 0x76) {
        var magic = Encoding.ASCII.GetString(file, 0, 4);
        if (magic is "PSID" or "RSID") {
          var version = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x04, 2));
          var dataOffset = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x06, 2));
          var loadAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x08, 2));
          var initAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x0A, 2));
          var playAddr = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x0C, 2));
          var songs = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x0E, 2));
          var startSong = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x10, 2));
          var speed = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(0x12, 4));
          var name = ReadCString(file, 0x16, 32);
          var author = ReadCString(file, 0x36, 32);
          var copyright = ReadCString(file, 0x56, 32);

          meta.Add("magic", magic);
          meta.Add("version", version);
          meta.Add("name", name);
          meta.Add("author", author);
          meta.Add("copyright", copyright);
          meta.Add("songs", songs);
          meta.Add("start_song", startSong);
          meta.Add("speed", speed);
          meta.Add("load_address", $"0x{loadAddr:X4}");
          meta.Add("init_address", $"0x{initAddr:X4}");
          meta.Add("play_address", $"0x{playAddr:X4}");

          // v2+ header (0x76..) carries flags / start-page / page-length.
          if (version >= 2 && file.Length >= 0x7C) {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(0x76, 2));
            meta.Add("flags", $"0x{flags:X4}");
            meta.Add("chip_model", DecodeChipModel((flags >> 4) & 0x03));
            meta.Add("clock", DecodeClock((flags >> 2) & 0x03));
            meta.Add("psid_specific", (flags & 0x02) != 0 ? "true" : "false");
          }

          // C64 program data begins at dataOffset; an optional 2-byte load address
          // prefix is included verbatim in the blob (we never reinterpret it).
          if (dataOffset > 0 && dataOffset <= file.Length)
            program = file.AsSpan(dataOffset).ToArray();

          ok = true;
        }
      }
    } catch { /* fall through to partial */ }

    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    if (program != null)
      entries.Add(new("program.prg", program, EntryKinds.Track));
    return entries;
  }

  private static string DecodeChipModel(int bits) => bits switch {
    1 => "MOS6581",
    2 => "MOS8580",
    3 => "MOS6581 and MOS8580",
    _ => "unknown",
  };

  private static string DecodeClock(int bits) => bits switch {
    1 => "PAL",
    2 => "NTSC",
    3 => "PAL and NTSC",
    _ => "unknown",
  };

  private static string ReadCString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) ++end;
    return Encoding.Latin1.GetString(data, offset, end - offset);
  }

  private sealed class IniBuilder(string section) {
    private readonly StringBuilder _sb = new StringBuilder().AppendLine($"[{section}]");
    public void Add(string key, long value) => _sb.Append(CultureInfo.InvariantCulture, $"{key} = {value}\n");
    public void Add(string key, string value) => _sb.Append(CultureInfo.InvariantCulture, $"{key} = {value}\n");
    public void AddStatus(bool ok) {
      if (!ok) _sb.Append("parse_status = partial\n");
    }
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(_sb.ToString());
  }
}
