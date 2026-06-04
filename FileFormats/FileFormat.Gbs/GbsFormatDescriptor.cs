#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Gbs;

/// <summary>
/// Surfaces a Game Boy Sound file (<c>.gbs</c>) as a metadata-rich pseudo-archive. GBS carries
/// a Game Boy CPU (LR35902) program that drives the DMG sound hardware; there is no audio to
/// decode, so the program image is surfaced verbatim as a Kind <c>Stream</c> blob.
/// <para>Layout: a 0x70-byte header (magic <c>GBS</c> + version 1, song counts, the
/// load/init/play vectors, stack pointer, timer modulo/control bytes, and three 32-byte
/// title/author/copyright strings) followed by the program loaded at <c>loadAddr</c>. The
/// program is surfaced as <c>program.bin</c>.</para>
/// Read-only; parsing degrades to FULL-only on malformed input.
/// </summary>
public sealed class GbsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Gbs";
  public string DisplayName => "Game Boy Sound";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gbs";
  public IReadOnlyList<string> Extensions => [".gbs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x47, 0x42, 0x53, 0x01], Confidence: 0.95), // "GBS" + version 1
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Game Boy Sound (.gbs); full file + header metadata + LR35902 program image.";

  private const int HeaderSize = 0x70;

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

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.gbs", "Container", blob),
    };

    if (blob.Length < HeaderSize)
      return entries;

    var version = blob[0x03];
    var numSongs = blob[0x04];
    var firstSong = blob[0x05];
    var loadAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x06));
    var initAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x08));
    var playAddr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0A));
    var stackPtr = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0x0C));
    var timerModulo = blob[0x0E];
    var timerControl = blob[0x0F];
    var title = ReadFixed(blob, 0x10, 32);
    var author = ReadFixed(blob, 0x30, 32);
    var copyright = ReadFixed(blob, 0x50, 32);

    var sb = new StringBuilder();
    sb.AppendLine("[gbs]");
    sb.AppendLine($"version={version}");
    sb.AppendLine($"num_songs={numSongs}");
    sb.AppendLine($"first_song={firstSong}");
    sb.AppendLine($"load_addr=0x{loadAddr:X4}");
    sb.AppendLine($"init_addr=0x{initAddr:X4}");
    sb.AppendLine($"play_addr=0x{playAddr:X4}");
    sb.AppendLine($"stack_ptr=0x{stackPtr:X4}");
    sb.AppendLine($"timer_modulo=0x{timerModulo:X2}");
    sb.AppendLine($"timer_control=0x{timerControl:X2}");
    AppendField(sb, "title", title);
    AppendField(sb, "author", author);
    AppendField(sb, "copyright", copyright);
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(sb.ToString())));

    if (blob.Length > HeaderSize)
      entries.Add(new("program.bin", "Stream", blob[HeaderSize..]));

    return entries;
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
