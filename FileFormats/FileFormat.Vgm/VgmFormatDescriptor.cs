#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Registry;

namespace FileFormat.Vgm;

/// <summary>
/// Surfaces a Video Game Music log (<c>.vgm</c> / gzip-compressed <c>.vgz</c>) as a
/// read-only pseudo-archive: <c>FULL.vgm</c> (the byte-exact, decompressed log),
/// <c>metadata.ini</c> (version, duration, and every populated chip clock), the
/// <c>commands.bin</c> command stream, and — when a GD3 tag block is present — its
/// eleven UTF-16LE fields as <c>metadata/gd3.ini</c>.
/// <para>The VGM header is little-endian. Versions below 1.50 use a fixed 0x40-byte
/// header; from 1.50 the data offset at <c>0x34</c> (relative to <c>0x34</c>) gives the
/// start of the command log. GD3 lives at the relative offset stored at <c>0x14</c>.</para>
/// Read-only — there is no synthesis back to a playable log.
/// </summary>
public sealed class VgmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Vgm";
  public string DisplayName => "Video Game Music Log";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vgm";
  public IReadOnlyList<string> Extensions => [".vgm", ".vgz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("Vgm "u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Video Game Music log; full file + chip-clock metadata + GD3 tags + command stream.";

  // Sample rate every VGM duration field is expressed in.
  private const int VgmSampleRate = 44100;

  // (offset, version-introduced ×100, label) for each documented chip-clock field.
  private static readonly (int Offset, int MinVersion, string Label)[] ChipClocks = [
    (0x0C, 100, "SN76489"),
    (0x10, 100, "YM2413"),
    (0x2C, 110, "YM2612"),
    (0x30, 110, "YM2151"),
    (0x38, 151, "SegaPCM"),
    (0x40, 151, "RF5C68"),
    (0x44, 151, "YM2203"),
    (0x48, 151, "YM2608"),
    (0x4C, 151, "YM2610"),
    (0x50, 151, "YM3812"),
    (0x54, 151, "YM3526"),
    (0x58, 151, "Y8950"),
    (0x5C, 151, "YMF262"),
    (0x60, 151, "YMF278B"),
    (0x64, 151, "YMF271"),
    (0x68, 151, "YMZ280B"),
    (0x6C, 151, "RF5C164"),
    (0x70, 151, "PWM"),
    (0x74, 151, "AY8910"),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── parsing ────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = Inflate(ReadAll(stream));

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.vgm", "Container", blob),
    };

    // A valid VGM needs at least the classic 0x40 header and the "Vgm " magic.
    if (blob.Length < 0x40 || blob[0] != 'V' || blob[1] != 'g' || blob[2] != 'm' || blob[3] != ' ')
      return entries;

    var version = ReadU32(blob, 0x08);
    var totalSamples = ReadU32(blob, 0x18);

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(BuildMetadataIni(blob, version, totalSamples))));

    // Command log: from the data offset (rel to 0x34; 0 → 0x40 for old versions) to GD3/EOF.
    var dataOffset = 0x40;
    if (version >= 150) {
      var rel = ReadU32(blob, 0x34);
      dataOffset = rel == 0 ? 0x40 : (int)(0x34 + rel);
    }

    var gd3Rel = ReadU32(blob, 0x14);
    var gd3Offset = gd3Rel == 0 ? 0 : (int)(0x14 + gd3Rel);

    var commandsEnd = gd3Offset > dataOffset ? gd3Offset : blob.Length;
    if (commandsEnd > blob.Length)
      commandsEnd = blob.Length;
    if (dataOffset >= 0 && dataOffset < commandsEnd) {
      var commands = blob[dataOffset..commandsEnd];
      entries.Add(new("commands.bin", "Stream", commands));
    }

    // GD3 tag block.
    var gd3 = TryBuildGd3Ini(blob, gd3Offset);
    if (gd3 != null)
      entries.Add(new("metadata/gd3.ini", "Tag", Encoding.UTF8.GetBytes(gd3)));

    return entries;
  }

  private static string BuildMetadataIni(byte[] blob, uint version, uint totalSamples) {
    var sb = new StringBuilder();
    sb.AppendLine("; VGM metadata");
    sb.Append("version=").AppendLine(FormatBcdVersion(version));
    var seconds = totalSamples / (double)VgmSampleRate;
    sb.Append("duration_seconds=").AppendLine(seconds.ToString("0.000", CultureInfo.InvariantCulture));
    sb.Append("total_samples=").AppendLine(totalSamples.ToString(CultureInfo.InvariantCulture));

    foreach (var (offset, minVersion, label) in ChipClocks) {
      if (version < (uint)minVersion || offset + 4 > blob.Length)
        continue;
      var clock = ReadU32(blob, offset);
      if (clock != 0)
        sb.Append(label).Append('=').AppendLine((clock & 0x3FFFFFFF).ToString(CultureInfo.InvariantCulture));
    }
    return sb.ToString();
  }

  /// <summary>VGM versions are BCD: 0x00000151 → "1.51".</summary>
  private static string FormatBcdVersion(uint version) {
    var major = (version >> 8) & 0xFF;
    var minor = version & 0xFF;
    return $"{major:X}.{minor:X2}";
  }

  // ── GD3 ────────────────────────────────────────────────────────────────────

  private static readonly string[] Gd3Keys = [
    "track_en", "track_jp", "game_en", "game_jp", "system_en", "system_jp",
    "author_en", "author_jp", "date", "ripper", "notes",
  ];

  private static string? TryBuildGd3Ini(byte[] blob, int gd3Offset) {
    if (gd3Offset <= 0 || gd3Offset + 12 > blob.Length)
      return null;
    if (blob[gd3Offset] != 'G' || blob[gd3Offset + 1] != 'd' || blob[gd3Offset + 2] != '3' || blob[gd3Offset + 3] != ' ')
      return null;

    var length = (int)ReadU32(blob, gd3Offset + 8);
    var dataStart = gd3Offset + 12;
    var dataEnd = dataStart + length;
    if (dataEnd > blob.Length)
      dataEnd = blob.Length;

    var sb = new StringBuilder();
    sb.AppendLine("; GD3 tag");
    var pos = dataStart;
    foreach (var key in Gd3Keys) {
      var value = ReadUtf16Field(blob, ref pos, dataEnd);
      if (value.Length > 0)
        sb.Append(key).Append('=').AppendLine(value);
    }
    return sb.ToString();
  }

  /// <summary>Reads one NUL-terminated UTF-16LE string and advances past the terminator.</summary>
  private static string ReadUtf16Field(byte[] blob, ref int pos, int end) {
    var start = pos;
    while (pos + 1 < end) {
      if (blob[pos] == 0 && blob[pos + 1] == 0) {
        var s = Encoding.Unicode.GetString(blob, start, pos - start);
        pos += 2;
        return s;
      }
      pos += 2;
    }
    // Unterminated trailing field: consume the remainder.
    var rest = Encoding.Unicode.GetString(blob, start, Math.Max(0, end - start));
    pos = end;
    return rest;
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>Transparently gunzips a .vgz (gzip magic 1F 8B); a raw .vgm passes through.</summary>
  private static byte[] Inflate(byte[] blob) {
    if (blob.Length < 2 || blob[0] != 0x1F || blob[1] != 0x8B)
      return blob;
    using var input = new MemoryStream(blob);
    using var gz = new GZipStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    gz.CopyTo(output);
    return output.ToArray();
  }

  private static uint ReadU32(byte[] blob, int offset)
    => offset + 4 <= blob.Length ? BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offset, 4)) : 0u;
}
