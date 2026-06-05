#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Smk;

/// <summary>
/// Surfaces a Smacker container (<c>.smk</c>, 'SMK2'/'SMK4') as a pseudo-archive that
/// extracts only its audio. The byte-exact original is <c>FULL.smk</c> (Kind
/// <c>Container</c>). The video data region is surfaced as <c>VIDEO.bin</c> (Kind
/// <c>Track</c>, Method <c>Stored</c>) and the header is summarised in <c>metadata.ini</c>
/// (Kind <c>Tag</c>). Each present audio track's concatenated chunks are surfaced as
/// <c>TRACKn.bin</c> (Kind <c>Stream</c>) and, for compressed Smacker audio (SMKA) or
/// uncompressed PCM, decoded to per-channel mono WAVs <c>TRACKn_&lt;CHANNEL&gt;.wav</c>
/// (Kind <c>Channel</c>) via <c>Codec.SmackerAudio</c> / PCM, with a graceful fallback to
/// the raw blob on any decode failure. Bink-audio-in-Smacker tracks remain blob-only.
/// Read-only; parsing degrades gracefully.
/// </summary>
public sealed class SmkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Smk";
  public string DisplayName => "Smacker Video";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".smk";
  public IReadOnlyList<string> Extensions => [".smk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SMK2"u8.ToArray(), Confidence: 0.95),
    new("SMK4"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("Stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Smacker video container (.smk); full file + video blob + per-track Smacker Audio channels.";

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
      new("FULL.smk", "Container", blob),
    };
    SmkReader.BuildEntries(blob, entries);
    return entries;
  }
}
