#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Sf2;

/// <summary>
/// Minimal SoundFont 2 (<c>RIFF/sfbk</c>) parser: reads the INFO sub-chunks,
/// the <c>sdta/smpl</c> 16-bit LE PCM block and the <c>pdta/shdr</c> sample headers
/// (plus <c>phdr</c> preset count). Tolerant of truncation — returns <c>null</c> only
/// when the file is not a recognizable sfbk bank.
/// </summary>
internal static class Sf2Reader {

  /// <summary>One <c>shdr</c> sample header (46 bytes on disk).</summary>
  internal readonly record struct SampleHeader(
    string Name, uint Start, uint End, uint LoopStart, uint LoopEnd,
    uint SampleRate, byte OriginalPitch, sbyte PitchCorrection, ushort SampleLink, ushort SampleType) {
    public bool IsRom => (SampleType & 0x8000) != 0;
    public bool IsEndMarker => Name.Equals("EOS", StringComparison.Ordinal) ||
                               (SampleType == 0 && Start == 0 && End == 0 && SampleRate == 0);
  }

  internal sealed class Bank {
    public required IReadOnlyList<(string Id, string Value)> Info { get; init; }
    public required byte[] SmplData { get; init; }
    public required IReadOnlyList<SampleHeader> SampleHeaders { get; init; }
    public int PresetCount { get; init; }
    public ushort VersionMajor { get; init; }
    public ushort VersionMinor { get; init; }
    public string BankName { get; init; } = "";
    public bool HasSm24 { get; init; }
  }

  // INFO sub-chunks that carry zero-terminated strings (ifil/iver are version words).
  private static readonly HashSet<string> StringInfoIds = new(StringComparer.Ordinal) {
    "isng", "INAM", "IROM", "ICRD", "IENG", "IPRD", "ICOP", "ICMT", "ISFT",
  };

  public static Bank? Parse(byte[] blob) {
    if (blob.Length < 12) return null;
    if (!blob.AsSpan(0, 4).SequenceEqual("RIFF"u8)) return null;
    if (!blob.AsSpan(8, 4).SequenceEqual("sfbk"u8)) return null;

    var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4));
    var end = Math.Min(blob.Length, 8L + riffSize);

    var info = new List<(string, string)>();
    byte[] smpl = [];
    var headers = new List<SampleHeader>();
    var presetCount = 0;
    ushort verMajor = 0, verMinor = 0;
    var bankName = "";
    var hasSm24 = false;

    // Walk the three top-level LIST chunks after the 12-byte RIFF/sfbk header.
    var pos = 12L;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, (int)pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      var bodyStart = pos + 8;
      var bodyEnd = Math.Min(end, bodyStart + size);
      if (bodyEnd < bodyStart) break;

      if (id == "LIST" && bodyEnd - bodyStart >= 4) {
        var listType = Encoding.ASCII.GetString(blob, (int)bodyStart, 4);
        var inner = bodyStart + 4;
        switch (listType) {
          case "INFO":
            ParseInfo(blob, inner, bodyEnd, info, ref verMajor, ref verMinor, ref bankName);
            break;
          case "sdta":
            ParseSdta(blob, inner, bodyEnd, ref smpl, ref hasSm24);
            break;
          case "pdta":
            ParsePdta(blob, inner, bodyEnd, headers, ref presetCount);
            break;
        }
      }

      // Advance with word-alignment padding.
      var advance = 8L + size + (size & 1);
      if (advance <= 0) break;
      pos += advance;
    }

    return new Bank {
      Info = info,
      SmplData = smpl,
      SampleHeaders = headers,
      PresetCount = presetCount,
      VersionMajor = verMajor,
      VersionMinor = verMinor,
      BankName = bankName,
      HasSm24 = hasSm24,
    };
  }

  private static void ParseInfo(byte[] blob, long start, long end,
      List<(string, string)> info, ref ushort verMajor, ref ushort verMinor, ref string bankName) {
    var pos = start;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, (int)pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      var bodyStart = pos + 8;
      var bodyEnd = Math.Min(end, bodyStart + size);
      if (bodyEnd < bodyStart) break;

      if (id is "ifil" or "iver" && bodyEnd - bodyStart >= 4) {
        var major = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)bodyStart));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan((int)bodyStart + 2));
        if (id == "ifil") { verMajor = major; verMinor = minor; }
        info.Add((id, $"{major}.{minor}"));
      } else if (StringInfoIds.Contains(id)) {
        var value = ReadZeroTerminated(blob, bodyStart, bodyEnd);
        info.Add((id, value));
        if (id == "INAM") bankName = value;
      }

      pos += 8 + size + (size & 1);
    }
  }

  private static void ParseSdta(byte[] blob, long start, long end, ref byte[] smpl, ref bool hasSm24) {
    var pos = start;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, (int)pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      var bodyStart = pos + 8;
      var bodyEnd = Math.Min(end, bodyStart + size);
      if (bodyEnd < bodyStart) break;

      if (id == "smpl") {
        smpl = new byte[bodyEnd - bodyStart];
        Buffer.BlockCopy(blob, (int)bodyStart, smpl, 0, smpl.Length);
      } else if (id == "sm24") {
        hasSm24 = true; // 24-bit low-byte extension; intentionally ignored.
      }

      pos += 8 + size + (size & 1);
    }
  }

  private static void ParsePdta(byte[] blob, long start, long end,
      List<SampleHeader> headers, ref int presetCount) {
    var pos = start;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, (int)pos, 4);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan((int)pos + 4));
      var bodyStart = pos + 8;
      var bodyEnd = Math.Min(end, bodyStart + size);
      if (bodyEnd < bodyStart) break;

      if (id == "shdr") {
        for (var rec = bodyStart; rec + 46 <= bodyEnd; rec += 46)
          headers.Add(ReadShdr(blob, (int)rec));
      } else if (id == "phdr") {
        // 38 bytes each; the last record is the terminal "EOP" sentinel.
        var count = (int)((bodyEnd - bodyStart) / 38);
        presetCount = Math.Max(0, count - 1);
      }

      pos += 8 + size + (size & 1);
    }
  }

  private static SampleHeader ReadShdr(byte[] blob, int off) {
    var name = ReadFixedString(blob, off, 20);
    var startIdx = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 20));
    var endIdx = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 24));
    var loopStart = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 28));
    var loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 32));
    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 36));
    var originalPitch = blob[off + 40];
    var pitchCorrection = (sbyte)blob[off + 41];
    var sampleLink = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 42));
    var sampleType = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 44));
    return new SampleHeader(name, startIdx, endIdx, loopStart, loopEnd, sampleRate,
      originalPitch, pitchCorrection, sampleLink, sampleType);
  }

  private static string ReadFixedString(byte[] blob, int off, int len) {
    var end = Math.Min(blob.Length, off + len);
    var sb = new StringBuilder(len);
    for (var i = off; i < end; ++i) {
      var b = blob[i];
      if (b == 0) break;
      sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static string ReadZeroTerminated(byte[] blob, long start, long end) {
    var sb = new StringBuilder();
    for (var i = start; i < end; ++i) {
      var b = blob[(int)i];
      if (b == 0) break;
      sb.Append((char)b);
    }
    return sb.ToString();
  }
}
