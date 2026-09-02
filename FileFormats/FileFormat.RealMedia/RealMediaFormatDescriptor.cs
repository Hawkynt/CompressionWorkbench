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
/// single audio payload as one stream blob plus metadata. RealAudio 14.4 (<c>lpcJ</c>/
/// <c>14_4</c>) streams are additionally decoded to a mono 8 kHz <c>*.MONO.wav</c>
/// (Kind <c>Channel</c>) via <c>Codec.Ra144</c>; cook / RealAudio G2 streams are
/// deinterleaved and decoded to per-channel WAVs (Kind <c>Channel</c>) via <c>Codec.Cook</c>;
/// both fall back to blob-only on any decode failure via try/catch. RealAudio 2.0 28.8 (<c>28_8</c>)
/// is Int4-deinterleaved and decoded to a mono 8 kHz WAV via <c>Codec.Ra288</c>; RealAudio Lossless
/// (<c>ralf</c>) is decoded to per-channel 16-bit WAVs via <c>Codec.Ralf</c>; sipr and atrc are
/// likewise decoded to per-channel WAVs. Read-only; every decode path falls back to blob-only on
/// failure and parsing degrades gracefully.
/// </summary>
public sealed class RealMediaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "RealMedia";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "RealMedia / RealAudio";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".rm";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".rm", ".rmvb", ".ra"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(".RMF"u8.ToArray(), Confidence: 0.95),
    new([0x2E, 0x72, 0x61, 0xFD], Confidence: 0.95), // ".ra\xFD" raw RealAudio
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "RealMedia (.rm/.rmvb) / RealAudio (.ra) container; full file + per-stream payloads + metadata.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
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
