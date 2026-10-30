using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pzx;

/// <summary>One PZX block: its four-letter tag and its payload.</summary>
public sealed record PzxBlock(string Tag, byte[] Data);

/// <summary>
/// PZX ("Perfect ZX Tape") 1.0 reader and writer, from Patrik Rak's format specification. The
/// writer emits the standard ROM-loader encoding exactly as pzxtools' <c>tap2pzx</c> does (MIT,
/// see <c>THIRD-PARTY-NOTICE.pzxtools.txt</c>).
/// </summary>
public static class PzxTape {
  public const int LeaderCycles = 2168, ShortLeader = 3223, LongLeader = 8063, Sync1 = 667, Sync2 = 735,
    Bit0 = 855, Bit1 = 1710, Tail = 945;

  /// <exception cref="InvalidDataException">Not PZX (no leading PZXT block), unsupported major version, or truncated.</exception>
  public static List<PzxBlock> Read(ReadOnlySpan<byte> file) {
    var blocks = new List<PzxBlock>();
    var o = 0;
    while (o < file.Length) {
      if (file.Length - o < 8) throw new InvalidDataException("PZX: truncated block header.");
      var tag = Encoding.ASCII.GetString(file.Slice(o, 4));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(file[(o + 4)..]);
      if (size > file.Length - o - 8) throw new InvalidDataException($"PZX: block {blocks.Count} ({tag}) runs past the end of the file.");
      var data = file.Slice(o + 8, (int)size).ToArray();
      if (blocks.Count == 0 && tag != "PZXT") throw new InvalidDataException("PZX: the file does not start with a PZXT header block.");
      if (tag == "PZXT" && (data.Length < 2 || data[0] != 1))
        throw new InvalidDataException($"PZX: unsupported major version {(data.Length > 0 ? data[0] : 0)}.");
      blocks.Add(new(tag, data));
      o += 8 + (int)size;
    }
    if (blocks.Count == 0) throw new InvalidDataException("PZX: empty file.");
    return blocks;
  }

  /// <summary>Title and key/value info strings of a PZXT block.</summary>
  public static (int Major, int Minor, string? Title, List<(string Key, string Value)> Info) Header(PzxBlock pzxt) {
    var strings = new List<string>();
    var rest = pzxt.Data.AsSpan(2);
    while (rest.Length > 0) {
      var nul = rest.IndexOf((byte)0);
      strings.Add(Encoding.UTF8.GetString(nul < 0 ? rest : rest[..nul]));
      rest = nul < 0 ? [] : rest[(nul + 1)..];
    }
    var info = new List<(string, string)>();
    for (var i = 1; i < strings.Count; i += 2)
      info.Add((strings[i], i + 1 < strings.Count ? strings[i + 1] : ""));
    return (pzxt.Data[0], pzxt.Data[1], strings.Count > 0 ? strings[0] : null, info);
  }

  /// <summary>
  /// The bytes of a DATA block that uses the standard ROM bit encoding (two pulses per bit,
  /// 855/855 for 0 and 1710/1710 for 1, within 2%, whole bytes), or null for any other block.
  /// </summary>
  public static byte[]? StandardData(PzxBlock block) {
    if (block.Tag != "DATA" || block.Data.Length < 8) return null;
    var d = block.Data;
    var bits = BinaryPrimitives.ReadUInt32LittleEndian(d) & 0x7FFFFFFF;
    int p0 = d[6], p1 = d[7];
    if (p0 != 2 || p1 != 2 || bits % 8 != 0 || d.Length < 8 + 8 + bits / 8) return null;
    static bool Near(int value, int nominal) => Math.Abs(value - nominal) * 50 <= nominal;
    for (var i = 0; i < 2; ++i) {
      if (!Near(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8 + 2 * i)), Bit0)) return null;
      if (!Near(BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12 + 2 * i)), Bit1)) return null;
    }
    return d.AsSpan(16, (int)(bits / 8)).ToArray();
  }

  /// <summary>Splits a ZX Spectrum TAP file (u16 length + block) into its blocks.</summary>
  public static List<byte[]> SplitTap(ReadOnlySpan<byte> tap) {
    var blocks = new List<byte[]>();
    for (var o = 0; o < tap.Length;) {
      if (tap.Length - o < 2) throw new InvalidDataException("TAP: truncated block length.");
      var size = BinaryPrimitives.ReadUInt16LittleEndian(tap[o..]);
      if (size > tap.Length - o - 2) throw new InvalidDataException("TAP: block runs past the end of the file.");
      if (size > 0) blocks.Add(tap.Slice(o + 2, size).ToArray());
      o += 2 + size;
    }
    return blocks;
  }

  /// <summary>Joins blocks into a TAP file.</summary>
  public static byte[] JoinTap(IEnumerable<byte[]> blocks) {
    using var ms = new MemoryStream();
    Span<byte> len = stackalloc byte[2];
    foreach (var b in blocks) {
      BinaryPrimitives.WriteUInt16LittleEndian(len, checked((ushort)b.Length));
      ms.Write(len);
      ms.Write(b);
    }
    return ms.ToArray();
  }

  /// <summary>
  /// Writes standard-loader blocks as tap2pzx does: a PZXT 1.0 header (plus the optional title),
  /// then per block a PULS (pilot, two sync pulses) and a DATA block (initial level high, 945-cycle tail).
  /// </summary>
  public static void WriteStandard(Stream output, IReadOnlyList<byte[]> blocks, string? title = null) {
    var header = new List<byte> { 1, 0 };
    if (!string.IsNullOrEmpty(title)) header.AddRange(Encoding.UTF8.GetBytes(title));
    WriteBlock(output, "PZXT", [.. header]);
    var word = new byte[2];
    foreach (var block in blocks) {
      using var puls = new MemoryStream();
      void U16(int v) { BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)v); puls.Write(word); }
      U16(0x8000 | (block.Length > 0 && block[0] < 128 ? LongLeader : ShortLeader));
      U16(LeaderCycles);
      U16(Sync1);
      U16(Sync2);
      WriteBlock(output, "PULS", puls.ToArray());

      var data = new byte[16 + block.Length];
      BinaryPrimitives.WriteUInt32LittleEndian(data, 0x80000000u | (uint)(8 * block.Length));
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), Tail);
      data[6] = 2;
      data[7] = 2;
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), Bit0);
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), Bit0);
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), Bit1);
      BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), Bit1);
      block.CopyTo(data, 16);
      WriteBlock(output, "DATA", data);
    }
  }

  private static void WriteBlock(Stream output, string tag, ReadOnlySpan<byte> data) {
    Span<byte> h = stackalloc byte[8];
    Encoding.ASCII.GetBytes(tag, h);
    BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)data.Length);
    output.Write(h);
    output.Write(data);
  }
}
