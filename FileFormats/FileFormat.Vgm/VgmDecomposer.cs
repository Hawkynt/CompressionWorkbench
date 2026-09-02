#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Vgm;

/// <summary>
/// Surfaces a VGM (Video Game Music) register-dump as a read-only pseudo-archive:
/// the verbatim file, parsed header metadata (version, active chips, sample counts,
/// loop), the GD3 tag (title/author/game/system in EN+JP) and the raw command
/// stream blob. Transparently gunzips the .vgz (gzip-wrapped) variant to read the
/// header while keeping FULL as the original (possibly gzipped) bytes. It never
/// emulates any sound chip and never throws from listing.
/// </summary>
public static class VgmDecomposer {

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

  // Map of header offset → (chip name) for the documented clock fields.
  private static readonly (int Offset, string Name)[] ChipClocks = [
    (0x0C, "SN76489"), (0x10, "YM2413"), (0x2C, "YM2612"), (0x30, "YM2151"),
    (0x38, "Sega PCM"), (0x40, "RF5C68"), (0x44, "YM2203"), (0x48, "YM2608"),
    (0x4C, "YM2610/B"), (0x50, "YM3812"), (0x54, "YM3526"), (0x58, "Y8950"),
    (0x5C, "YMF262"), (0x60, "YMF278B"), (0x64, "YMF271"), (0x68, "YMZ280B"),
    (0x6C, "RF5C164"), (0x70, "PWM"), (0x74, "AY8910"), (0x80, "GameBoy DMG"),
    (0x84, "NES APU"), (0x88, "MultiPCM"), (0x8C, "uPD7759"), (0x90, "OKIM6258"),
    (0x98, "OKIM6295"), (0x9C, "K051649"), (0xA0, "K054539"), (0xA4, "HuC6280"),
    (0xA8, "C140"), (0xAC, "K053260"), (0xB0, "Pokey"), (0xB4, "QSound"),
    (0xB8, "SCSP"), (0xC0, "WonderSwan"), (0xC4, "VSU"), (0xC8, "SAA1099"),
    (0xCC, "ES5503"), (0xD0, "ES5505/6"), (0xD8, "X1-010"), (0xDC, "C352"),
    (0xE0, "GA20"),
  ];

    /// <summary>
  /// Performs the decompose operation.
  /// </summary>
public static List<Entry> Decompose(byte[] file) {
    var entries = new List<Entry> { new("FULL.vgm", file, EntryKinds.Track) };
    var meta = new IniBuilder("vgm");
    var ok = false;

    // .vgz: gzip-wrapped VGM. Decompress only to read the header; FULL stays original.
    var gzipped = file.Length >= 2 && file[0] == 0x1F && file[1] == 0x8B;
    meta.Add("gzip_wrapped", gzipped ? "true" : "false");
    var data = gzipped ? TryGunzip(file) : file;

    var gd3 = (byte[]?)null;
    var commandStream = (byte[]?)null;

    try {
      if (data is { Length: >= 0x40 } &&
          BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x206D6756 /* "Vgm " */) {
        var eofOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x04, 4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x08, 4));
        var gd3RelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x14, 4));
        var totalSamples = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x18, 4));
        var loopRelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x1C, 4));
        var loopSamples = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x20, 4));

        meta.Add("version", $"{(version >> 8) & 0xFF:X1}.{version & 0xFF:X2}");
        meta.Add("eof_offset", eofOffset);
        meta.Add("total_samples", totalSamples);
        meta.Add("loop_samples", loopSamples);
        meta.Add("has_loop", loopRelOffset != 0 ? "true" : "false");

        // Data-stream start: VGM v1.50+ carries a relative VGM-data offset at 0x34.
        var dataStart = 0x40;
        if (version >= 0x150) {
          var dataRel = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x34, 4));
          if (dataRel != 0) dataStart = 0x34 + (int)dataRel;
        }

        var chips = new List<string>();
        foreach (var (off, name) in ChipClocks) {
          if (off + 4 > data.Length) continue;
          var clock = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off, 4)) & 0x3FFFFFFF;
          if (clock != 0) chips.Add(name);
        }
        meta.Add("active_chips", chips.Count > 0 ? string.Join(", ", chips) : "(none)");

        // GD3 tag block, when present.
        var gd3Abs = gd3RelOffset != 0 ? 0x14 + (int)gd3RelOffset : 0;
        if (gd3Abs > 0 && gd3Abs + 12 <= data.Length &&
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(gd3Abs, 4)) == 0x20336447 /* "Gd3 " */) {
          var gd3Len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(gd3Abs + 8, 4));
          var gd3End = gd3Abs + 12 + gd3Len;
          if (gd3Len >= 0 && gd3End <= data.Length)
            gd3 = data.AsSpan(gd3Abs, gd3End - gd3Abs).ToArray();
        }

        // Command stream: from dataStart up to the GD3 tag (or EOF).
        var streamEnd = gd3Abs > dataStart ? gd3Abs : data.Length;
        if (dataStart >= 0 && dataStart < streamEnd && streamEnd <= data.Length)
          commandStream = data.AsSpan(dataStart, streamEnd - dataStart).ToArray();

        ok = true;
      }
    } catch { /* fall through to partial */ }

    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));

    if (commandStream != null)
      entries.Add(new("command_stream.bin", commandStream, EntryKinds.Track));

    if (gd3 != null) {
      entries.Add(new("gd3.bin", gd3, EntryKinds.Tag));
      var gd3Ini = ParseGd3(gd3);
      if (gd3Ini != null)
        entries.Add(new("gd3.ini", gd3Ini, EntryKinds.Tag));
    }

    return entries;
  }

  // GD3 v1.00 payload: 11 NUL-terminated UTF-16LE strings (EN/JP pairs + notes).
  private static readonly string[] Gd3Fields = [
    "title_en", "title_jp", "game_en", "game_jp", "system_en", "system_jp",
    "author_en", "author_jp", "release_date", "vgm_by", "notes",
  ];

  private static byte[]? ParseGd3(byte[] gd3) {
    try {
      if (gd3.Length < 12) return null;
      var ini = new IniBuilder("gd3");
      var pos = 12; // skip "Gd3 " + version + length
      var idx = 0;
      var sb = new StringBuilder();
      while (pos + 1 < gd3.Length && idx < Gd3Fields.Length) {
        var ch = BinaryPrimitives.ReadUInt16LittleEndian(gd3.AsSpan(pos, 2));
        pos += 2;
        if (ch == 0) {
          ini.Add(Gd3Fields[idx], sb.ToString());
          sb.Clear();
          ++idx;
          continue;
        }
        sb.Append((char)ch);
      }
      return idx > 0 ? ini.ToBytes() : null;
    } catch {
      return null;
    }
  }

  private static byte[]? TryGunzip(byte[] file) {
    try {
      using var src = new MemoryStream(file);
      using var gz = new GZipStream(src, CompressionMode.Decompress);
      using var dst = new MemoryStream();
      gz.CopyTo(dst);
      return dst.ToArray();
    } catch {
      return null;
    }
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
