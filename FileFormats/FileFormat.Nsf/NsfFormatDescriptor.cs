#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Nsf;

/// <summary>
/// Surfaces an NES Sound Format file as a metadata-rich pseudo-archive. NSF carries a 6502
/// program that drives the NES APU (plus optional expansion sound chips); there is no audio
/// to decode, so the program image is surfaced verbatim as a Kind <c>Stream</c> blob alongside
/// the parsed header.
/// <para>Two on-disk variants are handled:</para>
/// <list type="bullet">
///   <item><b>NESM</b> (magic <c>NESM\x1A</c>): a fixed 0x80-byte header followed by the 6502
///     program loaded at <c>loadAddr</c>. The header carries song counts, the load/init/play
///     vectors, NTSC/PAL speed words, an 8-byte bankswitch table, a PAL/NTSC flag byte and the
///     expansion-chip flag byte (VRC6/VRC7/FDS/MMC5/N163/S5B). The program is surfaced as
///     <c>program.bin</c>.</item>
///   <item><b>NSFE</b> (magic <c>NSFE</c>): a chunked container (4CC + u32 LE size). The
///     <c>INFO</c> chunk holds the load/init/play vectors and chip flags, <c>DATA</c> is the
///     program (surfaced as <c>program.bin</c>), <c>auth</c> carries NUL-separated
///     title/artist/copyright/ripper strings, and every other chunk is surfaced verbatim as
///     <c>metadata/&lt;id&gt;.bin</c>.</item>
/// </list>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class NsfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Nsf";
  public string DisplayName => "NES Sound Format";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nsf";
  public IReadOnlyList<string> Extensions => [".nsf", ".nsfe"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("NESM\x1A"u8.ToArray(), Confidence: 0.95),
    new("NSFE"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "NES Sound Format (NESM/NSFE); full file + header metadata + 6502 program image.";

  private const int NesmHeaderSize = 0x80;

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var isNsfe = blob.Length >= 4 && blob[0] == 'N' && blob[1] == 'S' && blob[2] == 'F' && blob[3] == 'E';

    var entries = new List<AudioPseudoArchive.Entry> {
      new(isNsfe ? "FULL.nsfe" : "FULL.nsf", "Container", blob),
    };

    try {
      if (isNsfe)
        BuildNsfe(blob, entries);
      else
        BuildNesm(blob, entries);
    } catch {
      // Malformed input degrades to FULL-only.
    }

    return entries;
  }

  // ── classic NESM ───────────────────────────────────────────────────────────

  private static void BuildNesm(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    if (blob.Length < NesmHeaderSize)
      return;

    var version = blob[0x05];
    var totalSongs = blob[0x06];
    var startSong = blob[0x07];
    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x08));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0A));
    var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0C));
    var name = ReadFixed(blob, 0x0E, 32);
    var artist = ReadFixed(blob, 0x2E, 32);
    var copyright = ReadFixed(blob, 0x4E, 32);
    var ntscSpeed = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x6E));
    var palSpeed = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x78));
    var palNtscFlags = blob[0x7A];
    var extraChips = blob[0x7B];

    var bankswitched = false;
    for (var i = 0; i < 8; ++i)
      if (blob[0x70 + i] != 0)
        bankswitched = true;

    var sb = new StringBuilder();
    sb.AppendLine("[nsf]");
    sb.AppendLine("variant=NESM");
    sb.AppendLine($"version={version}");
    sb.AppendLine($"total_songs={totalSongs}");
    sb.AppendLine($"start_song={startSong}");
    AppendField(sb, "name", name);
    AppendField(sb, "artist", artist);
    AppendField(sb, "copyright", copyright);
    sb.AppendLine($"load_addr=0x{loadAddr:X4}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    sb.AppendLine($"play_addr=0x{playAddr:X4}");
    sb.AppendLine($"ntsc_speed={ntscSpeed}");
    sb.AppendLine($"pal_speed={palSpeed}");
    sb.AppendLine($"region={DescribeRegion(palNtscFlags)}");
    sb.AppendLine($"bankswitched={(bankswitched ? "true" : "false")}");
    if (bankswitched)
      sb.AppendLine($"bankswitch={FormatBankswitch(blob, 0x70)}");
    sb.AppendLine($"expansion_chips={DescribeChips(extraChips)}");
    sb.AppendLine($"expansion_flags=0x{extraChips:X2}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

    if (blob.Length > NesmHeaderSize)
      entries.Add(new("program.bin", "Stream", blob[NesmHeaderSize..]));
  }

  private static string DescribeRegion(byte flags) => (flags & 0x03) switch {
    0 => "NTSC",
    1 => "PAL",
    _ => "NTSC/PAL (dual)",
  };

  private static string FormatBankswitch(byte[] blob, int offset) {
    var parts = new string[8];
    for (var i = 0; i < 8; ++i)
      parts[i] = $"0x{blob[offset + i]:X2}";
    return string.Join(' ', parts);
  }

  // ── extended NSFE ────────────────────────────────────────────────────────

  private static void BuildNsfe(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    var sb = new StringBuilder();
    sb.AppendLine("[nsf]");
    sb.AppendLine("variant=NSFE");

    var pos = 4; // skip "NSFE"
    while (pos + 8 <= blob.Length) {
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos));
      var id = Encoding.ASCII.GetString(blob, pos + 4, 4);
      var dataStart = pos + 8;
      if (size < 0 || dataStart + size > blob.Length)
        break;
      var chunk = blob.AsSpan(dataStart, size);

      switch (id) {
        case "INFO":
          ParseInfoChunk(chunk, sb);
          break;
        case "DATA":
          entries.Add(new("program.bin", "Stream", chunk.ToArray()));
          break;
        case "auth":
          ParseAuthChunk(chunk, sb);
          break;
        case "NEND":
          AppendBinaryChunk(id, chunk, entries);
          pos = dataStart + size;
          goto done;
        default:
          AppendBinaryChunk(id, chunk, entries);
          break;
      }

      pos = dataStart + size;
    }

  done:
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));
  }

  private static void ParseInfoChunk(ReadOnlySpan<byte> chunk, StringBuilder sb) {
    if (chunk.Length >= 6) {
      sb.AppendLine($"load_addr=0x{BinaryPrimitives.ReadUInt16LittleEndian(chunk):X4}");
      sb.AppendLine($"init_addr=0x{BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]):X4}");
      sb.AppendLine($"play_addr=0x{BinaryPrimitives.ReadUInt16LittleEndian(chunk[4..]):X4}");
    }
    if (chunk.Length >= 7)
      sb.AppendLine($"region={DescribeRegion(chunk[6])}");
    if (chunk.Length >= 8) {
      sb.AppendLine($"expansion_chips={DescribeChips(chunk[7])}");
      sb.AppendLine($"expansion_flags=0x{chunk[7]:X2}");
    }
    if (chunk.Length >= 9)
      sb.AppendLine($"total_songs={chunk[8]}");
    if (chunk.Length >= 10)
      sb.AppendLine($"start_song={chunk[9]}");
  }

  private static void ParseAuthChunk(ReadOnlySpan<byte> chunk, StringBuilder sb) {
    var strings = SplitNul(chunk);
    string[] keys = ["name", "artist", "copyright", "ripper"];
    for (var i = 0; i < strings.Count && i < keys.Length; ++i)
      AppendField(sb, keys[i], strings[i]);
  }

  private static void AppendBinaryChunk(string id, ReadOnlySpan<byte> chunk, List<AudioPseudoArchive.Entry> entries) {
    var safe = new string([.. id.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')]);
    if (safe.Length == 0)
      safe = "chunk";
    entries.Add(new($"metadata/{safe}.bin", "Stream", chunk.ToArray()));
  }

  private static List<string> SplitNul(ReadOnlySpan<byte> data) {
    var result = new List<string>();
    var start = 0;
    for (var i = 0; i < data.Length; ++i) {
      if (data[i] != 0)
        continue;
      result.Add(Encoding.UTF8.GetString(data[start..i]));
      start = i + 1;
    }
    if (start < data.Length)
      result.Add(Encoding.UTF8.GetString(data[start..]));
    return result;
  }

  // ── shared ─────────────────────────────────────────────────────────────────

  /// <summary>Decodes the NSF expansion-chip flag byte into a human-readable chip list.</summary>
  private static string DescribeChips(byte flags) {
    if (flags == 0)
      return "none";
    var chips = new List<string>();
    if ((flags & 0x01) != 0) chips.Add("VRC6");
    if ((flags & 0x02) != 0) chips.Add("VRC7");
    if ((flags & 0x04) != 0) chips.Add("FDS");
    if ((flags & 0x08) != 0) chips.Add("MMC5");
    if ((flags & 0x10) != 0) chips.Add("N163");
    if ((flags & 0x20) != 0) chips.Add("S5B");
    return chips.Count > 0 ? string.Join(", ", chips) : "none";
  }

  private static string ReadFixed(byte[] blob, int offset, int length) {
    if (offset + length > blob.Length)
      length = Math.Max(0, blob.Length - offset);
    var raw = blob.AsSpan(offset, length);
    var end = raw.IndexOf((byte)0);
    if (end < 0)
      end = raw.Length;
    return Encoding.Latin1.GetString(raw[..end]).Trim();
  }

  private static void AppendField(StringBuilder sb, string key, string value) {
    value = value.Trim();
    if (value.Length > 0)
      sb.AppendLine($"{key}={value}");
  }
}
