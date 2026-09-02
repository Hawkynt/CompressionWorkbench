#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Bik;

/// <summary>
/// Surfaces a Bink video container (<c>.bik</c>, Bink 1 'BIK?' and Bink 2 'KB2?') as a
/// pseudo-archive that extracts only its audio. The byte-exact original is
/// <c>FULL.bik</c> (Kind <c>Container</c>). The video data region is surfaced as
/// <c>VIDEO.bin</c> (Kind <c>Track</c>, Method <c>Stored</c>) and the header is summarised
/// in <c>metadata.ini</c> (Kind <c>Tag</c>). Each audio track's concatenated packets are
/// surfaced as <c>TRACKn.bin</c> (Kind <c>Stream</c>, Method = the Bink Audio flavour) and,
/// for Bink 1, decoded to per-channel mono WAVs <c>TRACKn_&lt;CHANNEL&gt;.wav</c>
/// (Kind <c>Channel</c>) via <c>Codec.BinkAudio</c> — both RDFT and DCT flavours — with a
/// graceful fallback to the raw blob on any decode failure. Bink 2 audio is not decoded and
/// remains blob-only. Read-only; parsing degrades gracefully.
/// </summary>
public sealed class BikFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Bik";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Bink Video";
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
public string DefaultExtension => ".bik";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".bik", ".bk2"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Bink 1: 'BIK' + revision letter b/f/g/h/i/k.
    new("BIKb"u8.ToArray(), Confidence: 0.95),
    new("BIKf"u8.ToArray(), Confidence: 0.95),
    new("BIKg"u8.ToArray(), Confidence: 0.95),
    new("BIKh"u8.ToArray(), Confidence: 0.95),
    new("BIKi"u8.ToArray(), Confidence: 0.95),
    new("BIKk"u8.ToArray(), Confidence: 0.95),
    // Bink 2: 'KB2' + revision letter a/d/f/g/h/i/j/k (audio surfaced as blob only).
    new("KB2a"u8.ToArray(), Confidence: 0.9),
    new("KB2d"u8.ToArray(), Confidence: 0.9),
    new("KB2f"u8.ToArray(), Confidence: 0.9),
    new("KB2g"u8.ToArray(), Confidence: 0.9),
    new("KB2h"u8.ToArray(), Confidence: 0.9),
    new("KB2i"u8.ToArray(), Confidence: 0.9),
    new("KB2j"u8.ToArray(), Confidence: 0.9),
    new("KB2k"u8.ToArray(), Confidence: 0.9),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("Stored", "Stored")];
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
public string Description => "Bink video container (.bik/.bk2); full file + video blob + per-track Bink Audio channels.";

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

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.bik", "Container", blob),
    };
    BikReader.BuildEntries(blob, entries);
    return entries;
  }
}
