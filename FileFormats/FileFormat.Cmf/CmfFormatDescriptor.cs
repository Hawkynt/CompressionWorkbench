#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Cmf;

/// <summary>
/// Surfaces a Creative Music File (<c>.cmf</c>, OPL) as a read-only pseudo-archive:
/// <c>FULL.cmf</c> (the byte-exact file), <c>metadata.ini</c> (title, composer,
/// remarks, tempi), one 16-byte OPL register patch per instrument under
/// <c>instruments/NN.bin</c>, and <c>music.mid</c> — the CMF music event stream
/// wrapped in a Standard MIDI File (format 0). Falls back to FULL-only on a malformed
/// header.
/// <para>The CMF header is little-endian: u16 version, then byte offsets to the
/// instrument block, music block, and three optional NUL-terminated strings, plus the
/// OPL timing fields and instrument count.</para>
/// </summary>
public sealed class CmfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Cmf";
  public string DisplayName => "Creative Music File (OPL)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".cmf";
  public IReadOnlyList<string> Extensions => [".cmf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("CTMF"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Creative Music File (OPL); full file + OPL patches + CMF→MIDI music + metadata.";

  private const int OplPatchSize = 16;

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── parsing ────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.cmf", "Container", blob),
    };

    if (blob.Length < 40 || blob[0] != 'C' || blob[1] != 'T' || blob[2] != 'M' || blob[3] != 'F')
      return entries;

    var version = ReadU16(blob, 0x04);
    var instrumentOffset = ReadU16(blob, 0x06);
    var musicOffset = ReadU16(blob, 0x08);
    var ticksPerQuarter = ReadU16(blob, 0x0A);
    var ticksPerSecond = ReadU16(blob, 0x0C);
    var titleOffset = ReadU16(blob, 0x0E);
    var composerOffset = ReadU16(blob, 0x10);
    var remarksOffset = ReadU16(blob, 0x12);
    var numInstruments = ReadU16(blob, 0x24);
    var basicTempo = ReadU16(blob, 0x26);

    var title = ReadCString(blob, titleOffset);
    var composer = ReadCString(blob, composerOffset);
    var remarks = ReadCString(blob, remarksOffset);

    var ini = new StringBuilder();
    ini.AppendLine("; CMF metadata");
    ini.Append("version=").Append((version >> 8) & 0xFF).Append('.')
       .AppendLine((version & 0xFF).ToString("D2", CultureInfo.InvariantCulture));
    if (title.Length > 0) ini.Append("title=").AppendLine(title);
    if (composer.Length > 0) ini.Append("composer=").AppendLine(composer);
    if (remarks.Length > 0) ini.Append("remarks=").AppendLine(remarks);
    ini.Append("ticks_per_quarter=").AppendLine(ticksPerQuarter.ToString(CultureInfo.InvariantCulture));
    ini.Append("ticks_per_second=").AppendLine(ticksPerSecond.ToString(CultureInfo.InvariantCulture));
    ini.Append("basic_tempo=").AppendLine(basicTempo.ToString(CultureInfo.InvariantCulture));
    ini.Append("instruments=").AppendLine(numInstruments.ToString(CultureInfo.InvariantCulture));
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(ini.ToString())));

    // 16-byte OPL register patches.
    for (var i = 0; i < numInstruments; ++i) {
      var start = instrumentOffset + i * OplPatchSize;
      if (start + OplPatchSize > blob.Length)
        break;
      entries.Add(new($"instruments/{i:D2}.bin", "Stream", blob[start..(start + OplPatchSize)]));
    }

    // Music event stream → SMF type-0.
    if (musicOffset > 0 && musicOffset < blob.Length) {
      var music = blob[musicOffset..];
      var division = ticksPerQuarter == 0 ? 96 : ticksPerQuarter;
      entries.Add(new("music.mid", "Track", WrapAsSmf(music, division)));
    }

    return entries;
  }

  /// <summary>
  /// Wraps the raw CMF music event bytes (standard MIDI track data) in a format-0 SMF,
  /// appending an end-of-track meta-event when the stream does not already end with one.
  /// </summary>
  private static byte[] WrapAsSmf(byte[] music, int division) {
    var body = new List<byte>(music.Length + 4);
    body.AddRange(music);
    var hasEot = music.Length >= 3 && music[^3] == 0xFF && music[^2] == 0x2F && music[^1] == 0x00;
    if (!hasEot)
      body.AddRange([0x00, 0xFF, 0x2F, 0x00]);

    using var ms = new MemoryStream();
    ms.Write("MThd"u8);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(u32, 6);
    ms.Write(u32);
    Span<byte> hdr = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16BigEndian(hdr[0..], 0);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[2..], 1);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], (ushort)division);
    ms.Write(hdr);
    ms.Write("MTrk"u8);
    BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)body.Count);
    ms.Write(u32);
    ms.Write(body.ToArray());
    return ms.ToArray();
  }

  private static ushort ReadU16(byte[] blob, int offset)
    => offset + 2 <= blob.Length ? BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(offset, 2)) : (ushort)0;

  private static string ReadCString(byte[] blob, int offset) {
    if (offset <= 0 || offset >= blob.Length)
      return string.Empty;
    var end = offset;
    while (end < blob.Length && blob[end] != 0)
      ++end;
    return Encoding.Latin1.GetString(blob, offset, end - offset);
  }
}
