#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gym;

/// <summary>
/// Exposes a Sega Genesis YM2612 log (.gym) as a read-only pseudo-archive of
/// <c>FULL.gym</c>, <c>metadata.ini</c> and <c>command_stream.bin</c> (the raw
/// register log). Both the 428-byte "GYMX" header variant (carrying title / author /
/// game tags) and the headerless raw-command variant are handled. The YM2612 / PSG
/// are never emulated; all reads are clamped and a malformed file surfaces
/// FULL + metadata(parse_status=partial) instead of throwing. Headerless files are
/// detected by the <c>.gym</c> extension.
/// </summary>
public sealed class GymFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Gym";
  public string DisplayName => "GYM (Genesis YM2612 Log)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gym";
  public IReadOnlyList<string> Extensions => [".gym"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // "GYMX" marks the optional header; headerless dumps have no magic and rely on
  // the .gym extension, so the signature is intentionally low-confidence.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("GYMX"u8.ToArray(), Offset: 0, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Sega Genesis YM2612 register log surfaced as a read-only pseudo-archive " +
    "(FULL + metadata + raw command stream); both the GYMX-header and headerless " +
    "variants are handled. Never emulated.";

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

  // GYMX header layout (428 bytes): "GYMX" + Song(32) + Game(32) + Copyright(32)
  //  + Emulator(32) + Dumper(32) + Comment(256) + LoopStart(u32) + CompressedSize(u32).
  private const int HeaderSize = 428;

  private static List<Entry> Decompose(byte[] f) {
    var entries = new List<Entry> { new("FULL.gym", f, "Track") };
    var meta = new StringBuilder().AppendLine("[gym]");
    var ok = false;

    try {
      var hasHeader = f.Length >= HeaderSize &&
                      f[0] == 'G' && f[1] == 'Y' && f[2] == 'M' && f[3] == 'X';
      meta.Append("has_header = ").Append(hasHeader ? "true" : "false").Append('\n');

      var streamStart = 0;
      if (hasHeader) {
        meta.Append("song = ").Append(ReadAscii(f, 0x04, 32)).Append('\n');
        meta.Append("game = ").Append(ReadAscii(f, 0x24, 32)).Append('\n');
        meta.Append("copyright = ").Append(ReadAscii(f, 0x44, 32)).Append('\n');
        meta.Append("emulator = ").Append(ReadAscii(f, 0x64, 32)).Append('\n');
        meta.Append("dumper = ").Append(ReadAscii(f, 0x84, 32)).Append('\n');
        meta.Append("comment = ").Append(ReadAscii(f, 0xA4, 256)).Append('\n');
        streamStart = HeaderSize;
      }

      if (streamStart < f.Length)
        entries.Add(new("command_stream.bin", f.AsSpan(streamStart).ToArray(), "Track"));
      meta.Append("command_stream_bytes = ").Append(f.Length - streamStart).Append('\n');
      ok = true;
    } catch { /* fall through to partial */ }

    if (!ok) meta.Append("parse_status = partial\n");
    entries.Insert(1, new("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString()), "Tag"));
    return entries;
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
