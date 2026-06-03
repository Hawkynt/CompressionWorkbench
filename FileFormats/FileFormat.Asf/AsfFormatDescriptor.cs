#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Asf;

/// <summary>
/// Surfaces a Microsoft Advanced Systems Format container (<c>.asf</c>/<c>.wma</c>/
/// <c>.wmv</c>) as an archive of the byte-exact original (<c>FULL.asf</c>, Kind
/// <c>Container</c>) plus rich metadata and a description of each carried stream.
/// ASF packets are intricate to depayload into per-stream elementary bitstreams, so
/// the whole Data Object payload is surfaced verbatim as <c>data/packets.bin</c>
/// (Kind <c>Stream</c>) and each stream is described in
/// <c>streams/stream_NN.info.txt</c> (Kind <c>Tag</c>) carrying its codec / bitrate.
/// File properties and the content description land in <c>metadata.ini</c>; the
/// Extended Content Description tags land in <c>metadata/tags.ini</c>. Read-only;
/// parsing stops gracefully on a malformed object, keeping whatever was read.
/// </summary>
public sealed class AsfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Asf";
  public string DisplayName => "ASF (Advanced Systems Format)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".asf";
  public IReadOnlyList<string> Extensions => [".asf", ".wma", ".wmv"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASF Header Object GUID (little-endian byte order on disk).
    new([0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
         0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ASF/WMA/WMV container; full file + metadata + per-stream descriptions + packet payload.";

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
      new("FULL.asf", "Container", blob),
    };

    var parsed = AsfReader.Parse(blob);

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(parsed.RenderMetadataIni())));

    if (parsed.ExtendedTags.Count > 0)
      entries.Add(new("metadata/tags.ini", "Tag", Encoding.UTF8.GetBytes(parsed.RenderTagsIni())));

    foreach (var s in parsed.Streams)
      entries.Add(new($"streams/stream_{s.StreamNumber:D2}.info.txt", "Tag",
        Encoding.UTF8.GetBytes(s.Render())));

    if (parsed.DataPayload is { Length: > 0 } payload)
      entries.Add(new("data/packets.bin", "Stream", payload, Method: "asf_packets"));

    return entries;
  }
}
