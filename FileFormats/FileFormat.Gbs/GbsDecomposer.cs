#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Gbs;

/// <summary>
/// Surfaces a GBS (Game Boy Sound) file as a read-only pseudo-archive: the
/// verbatim file, parsed header metadata — name, author, copyright, song count and
/// the load/init/play/stack/timer addresses — plus the raw code+data blob. It
/// never emulates the Game Boy CPU or sound hardware and never throws from listing.
/// </summary>
public static class GbsDecomposer {

  public static class EntryKinds {
    public const string Track = "Track";
    public const string Tag = "Tag";
  }

  public readonly record struct Entry(string Name, byte[] Data, string Kind);

  // The fixed GBS header is 0x70 bytes; code+data follows immediately.
  private const int HeaderSize = 0x70;

  public static List<Entry> Decompose(byte[] file) {
    var entries = new List<Entry> { new("FULL.gbs", file, EntryKinds.Track) };
    var meta = new IniBuilder("gbs");
    var ok = false;
    var program = (byte[]?)null;

    try {
      if (file.Length >= HeaderSize && file[0] == 'G' && file[1] == 'B' && file[2] == 'S') {
        var version = file[0x03];
        var songs = file[0x04];
        var firstSong = file[0x05];
        var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x06, 2));
        var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x08, 2));
        var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x0A, 2));
        var stackPtr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x0C, 2));
        var timerModulo = file[0x0E];
        var timerControl = file[0x0F];
        var name = ReadCString(file, 0x10, 32);
        var author = ReadCString(file, 0x30, 32);
        var copyright = ReadCString(file, 0x50, 32);

        meta.Add("version", version);
        meta.Add("name", name);
        meta.Add("author", author);
        meta.Add("copyright", copyright);
        meta.Add("songs", songs);
        meta.Add("first_song", firstSong);
        meta.Add("load_address", $"0x{loadAddr:X4}");
        meta.Add("init_address", $"0x{initAddr:X4}");
        meta.Add("play_address", $"0x{playAddr:X4}");
        meta.Add("stack_pointer", $"0x{stackPtr:X4}");
        meta.Add("timer_modulo", $"0x{timerModulo:X2}");
        meta.Add("timer_control", $"0x{timerControl:X2}");

        if (file.Length > HeaderSize)
          program = file.AsSpan(HeaderSize).ToArray();

        ok = true;
      }
    } catch { /* fall through to partial */ }

    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    if (program != null)
      entries.Add(new("program.bin", program, EntryKinds.Track));
    return entries;
  }

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
