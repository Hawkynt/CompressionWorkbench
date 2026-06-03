#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.RealMedia;

/// <summary>
/// Surfaces a RealMedia container (<c>.rm</c>/<c>.rmvb</c>) or a raw RealAudio file
/// (<c>.ra</c>) as an archive. The byte-exact original is <c>FULL.rm</c>/<c>FULL.ra</c>
/// (Kind <c>Container</c>). For <c>.RMF</c> containers each stream's depayloaded
/// packet bytes are concatenated into <c>streams/stream_NN.bin</c> (Kind <c>Stream</c>,
/// Method = the detected codec FOURCC); the CONT chunk's title/author/copyright/comment
/// become <c>metadata.ini</c> (Kind <c>Tag</c>) and per-stream MDPR properties become
/// <c>streams/stream_NN.info.txt</c> (Kind <c>Tag</c>). Raw <c>.ra</c> surfaces its
/// single audio payload as one stream blob plus metadata. Read-only; no audio decode
/// (cook/sipr/atrc/… are out of scope); parsing degrades gracefully.
/// </summary>
public sealed class RealMediaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "RealMedia";
  public string DisplayName => "RealMedia / RealAudio";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".rm";
  public IReadOnlyList<string> Extensions => [".rm", ".rmvb", ".ra"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(".RMF"u8.ToArray(), Confidence: 0.95),
    new([0x2E, 0x72, 0x61, 0xFD], Confidence: 0.95), // ".ra\xFD" raw RealAudio
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "RealMedia (.rm/.rmvb) / RealAudio (.ra) container; full file + per-stream payloads + metadata.";

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

    var isRawRa = blob.Length >= 4 && blob[0] == 0x2E && blob[1] == 0x72 && blob[2] == 0x61 && blob[3] == 0xFD;

    var entries = new List<AudioPseudoArchive.Entry> {
      new(isRawRa ? "FULL.ra" : "FULL.rm", "Container", blob),
    };

    if (isRawRa)
      RealMediaReader.BuildRawRaEntries(blob, entries);
    else
      RealMediaReader.BuildRmfEntries(blob, entries);

    return entries;
  }
}
