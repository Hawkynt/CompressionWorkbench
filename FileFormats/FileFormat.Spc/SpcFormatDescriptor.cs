#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Spc;

/// <summary>
/// Exposes a SNES SPC700 sound dump (.spc) as a read-only pseudo-archive of
/// <c>FULL.spc</c>, <c>metadata.ini</c> (ID666 tags + SPC700 registers),
/// <c>ram.bin</c> (the 64&#160;KB APU RAM dump) and <c>dsp_registers.bin</c> (the
/// 128-byte DSP register block). Both the text and binary ID666 tag layouts are
/// handled. The SPC700 / DSP are never emulated; all reads are clamped and a
/// malformed file surfaces FULL + metadata(parse_status=partial) instead of throwing.
/// </summary>
public sealed class SpcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  private static readonly byte[] MagicBytes = "SNES-SPC700 Sound File Data v0.30"u8.ToArray();

  public string Id => "Spc";
  public string DisplayName => "SNES SPC700 Sound Dump";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".spc";
  public IReadOnlyList<string> Extensions => [".spc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SNES-SPC700 Sound File Data"u8.ToArray(), Offset: 0, Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "SNES SPC700 sound dump surfaced as a read-only pseudo-archive (FULL + ID666 " +
    "metadata + SPC700 registers + 64KB RAM + DSP registers); never emulated.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    Decompose(ReadAll(stream)).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null, e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in Decompose(ReadAll(stream))) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private readonly record struct Entry(string Name, byte[] Data, string Kind);

  private static List<Entry> Decompose(byte[] f) {
    var entries = new List<Entry> { new("FULL.spc", f, "Track") };
    var meta = new StringBuilder().AppendLine("[spc]");
    var ok = false;

    try {
      if (f.Length >= 0x10100 && f.AsSpan(0, MagicBytes.Length).SequenceEqual(MagicBytes)) {
        // Header registers: PC@0x25 (u16 LE), A@0x27, X@0x28, Y@0x29, PSW@0x2A, SP@0x2B.
        var hasId666 = f[0x23] == 26; // 0x1A => contains ID666 tag
        meta.Append("has_id666_tag = ").Append(hasId666 ? "true" : "false").Append('\n');
        meta.Append("reg_pc = 0x").Append((f[0x25] | (f[0x26] << 8)).ToString("X4")).Append('\n');
        meta.Append("reg_a = 0x").Append(f[0x27].ToString("X2")).Append('\n');
        meta.Append("reg_x = 0x").Append(f[0x28].ToString("X2")).Append('\n');
        meta.Append("reg_y = 0x").Append(f[0x29].ToString("X2")).Append('\n');
        meta.Append("reg_psw = 0x").Append(f[0x2A].ToString("X2")).Append('\n');
        meta.Append("reg_sp = 0x").Append(f[0x2B].ToString("X2")).Append('\n');

        if (hasId666)
          ReadId666(f, meta);

        // 64 KB SPC700 RAM dump at 0x100.
        entries.Add(new("ram.bin", f.AsSpan(0x100, 0x10000).ToArray(), "Track"));
        // 128-byte DSP register block at 0x10100.
        var dspLen = Math.Min(128, f.Length - 0x10100);
        if (dspLen > 0)
          entries.Add(new("dsp_registers.bin", f.AsSpan(0x10100, dspLen).ToArray(), "Tag"));

        ok = true;
      }
    } catch { /* fall through to partial */ }

    if (!ok) meta.Append("parse_status = partial\n");
    entries.Insert(1, new("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString()), "Tag"));
    return entries;
  }

  private static void ReadId666(byte[] f, StringBuilder meta) {
    // ID666 tag begins at 0x2E. Text and binary variants differ only from the
    // dump-date field onward. Detect by inspecting the date region: in the text
    // layout 0x9E..0xA8 holds an ASCII "MM/DD/YYYY" date (digits, '/', or zeros);
    // in the binary layout those bytes are packed integers (often non-printable).
    var binary = IsBinaryTag(f);
    meta.Append("id666_format = ").Append(binary ? "binary" : "text").Append('\n');

    meta.Append("song_title = ").Append(ReadAscii(f, 0x2E, 32)).Append('\n');
    meta.Append("game_title = ").Append(ReadAscii(f, 0x4E, 32)).Append('\n');
    meta.Append("dumper = ").Append(ReadAscii(f, 0x6E, 16)).Append('\n');
    meta.Append("comments = ").Append(ReadAscii(f, 0x7E, 32)).Append('\n');

    if (binary) {
      // Binary layout: artist string at 0xB1 (32 bytes).
      meta.Append("artist = ").Append(ReadAscii(f, 0xB1, 32)).Append('\n');
    } else {
      // Text layout: dump date 0x9E (11), song length 0xA9 (3), fade 0xAC (5),
      // artist 0xB1 (32).
      meta.Append("dump_date = ").Append(ReadAscii(f, 0x9E, 11)).Append('\n');
      meta.Append("song_length_seconds = ").Append(ReadAscii(f, 0xA9, 3)).Append('\n');
      meta.Append("artist = ").Append(ReadAscii(f, 0xB1, 32)).Append('\n');
    }
  }

  private static bool IsBinaryTag(byte[] f) {
    // Heuristic: scan the text date field 0x9E..0xA8. If every non-zero byte is a
    // digit or '/', treat as text; otherwise binary.
    for (var i = 0x9E; i <= 0xA8 && i < f.Length; ++i) {
      var b = f[i];
      if (b == 0) continue;
      var isDateChar = (b is >= (byte)'0' and <= (byte)'9') || b == (byte)'/' || b == (byte)' ';
      if (!isDateChar) return true;
    }
    return false;
  }

  private static string ReadAscii(byte[] f, int off, int maxLen) {
    var end = Math.Min(off + maxLen, f.Length);
    var sb = new StringBuilder();
    for (var i = off; i < end; ++i) {
      var b = f[i];
      if (b == 0) break;
      if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }
}
