#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Nsf;

/// <summary>
/// Surfaces an NSF (NES Sound Format) file as a read-only pseudo-archive: the
/// verbatim file, parsed header metadata — name, artist, copyright, song count,
/// region and expansion sound chips — plus the raw 6502 program/data blob. It
/// never emulates the 6502 or any expansion chip and never throws from listing.
/// </summary>
public static class NsfDecomposer {

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

  // Expansion-chip bit → name (header byte 0x7B).
  private static readonly (int Bit, string Name)[] Expansions = [
    (0, "VRC6"), (1, "VRC7"), (2, "FDS"), (3, "MMC5"),
    (4, "Namco163"), (5, "Sunsoft 5B"), (6, "VT02+"),
  ];

    /// <summary>
  /// Performs the decompose operation.
  /// </summary>
public static List<Entry> Decompose(byte[] file) {
    var entries = new List<Entry> { new("FULL.nsf", file, EntryKinds.Track) };
    var meta = new IniBuilder("nsf");
    var ok = false;
    var program = (byte[]?)null;

    try {
      if (file.Length >= 0x80 &&
          file[0] == 'N' && file[1] == 'E' && file[2] == 'S' && file[3] == 'M' && file[4] == 0x1A) {
        var version = file[0x05];
        var totalSongs = file[0x06];
        var startingSong = file[0x07];
        var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x08, 2));
        var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x0A, 2));
        var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x0C, 2));
        var name = ReadCString(file, 0x0E, 32);
        var artist = ReadCString(file, 0x2E, 32);
        var copyright = ReadCString(file, 0x4E, 32);
        var ntscSpeed = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x6E, 2));
        var palSpeed = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x78, 2));
        var regionFlags = file[0x7A];
        var expansionFlags = file[0x7B];

        meta.Add("version", version);
        meta.Add("name", name);
        meta.Add("artist", artist);
        meta.Add("copyright", copyright);
        meta.Add("songs", totalSongs);
        meta.Add("starting_song", startingSong);
        meta.Add("load_address", $"0x{loadAddr:X4}");
        meta.Add("init_address", $"0x{initAddr:X4}");
        meta.Add("play_address", $"0x{playAddr:X4}");
        meta.Add("ntsc_speed", ntscSpeed);
        meta.Add("pal_speed", palSpeed);
        meta.Add("region", DecodeRegion(regionFlags));

        var hasBankswitch = false;
        for (var i = 0; i < 8; i++)
          if (file[0x70 + i] != 0) hasBankswitch = true;
        meta.Add("bankswitched", hasBankswitch ? "true" : "false");

        var chips = new List<string>();
        foreach (var (bit, cname) in Expansions)
          if ((expansionFlags & (1 << bit)) != 0) chips.Add(cname);
        meta.Add("expansion_chips", chips.Count > 0 ? string.Join(", ", chips) : "(none)");

        // Program/data follows the 128-byte header.
        if (file.Length > 0x80)
          program = file.AsSpan(0x80).ToArray();

        ok = true;
      }
    } catch { /* fall through to partial */ }

    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    if (program != null)
      entries.Add(new("program.bin", program, EntryKinds.Track));
    return entries;
  }

  private static string DecodeRegion(byte flags) {
    if ((flags & 0x02) != 0) return "PAL/NTSC dual";
    return (flags & 0x01) != 0 ? "PAL" : "NTSC";
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
