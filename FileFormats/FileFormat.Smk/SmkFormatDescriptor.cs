#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Smk;

/// <summary>
/// Surfaces a Smacker container (<c>.smk</c>, 'SMK2'/'SMK4') as a pseudo-archive. The
/// byte-exact original is <c>FULL.smk</c> (Kind <c>Container</c>); lossless remux structure
/// is exposed through <c>HEADER.bin</c>, <c>FRAME_SIZES.bin</c>, <c>FRAME_TYPES.bin</c>,
/// <c>HUFFMAN.bin</c> and the physical frame-data region <c>VIDEO.bin</c>. Each audio track
/// is additionally surfaced as a frame-addressed <c>TRACKn.packets</c> bundle and as the
/// legacy concatenated <c>TRACKn.bin</c> stream, with SMKA/PCM tracks decoded to WAV channels
/// when possible. Creation rebuilds the container around already encoded video/Huffman data
/// and permits packet-preserving audio replacement; it deliberately does not claim to encode
/// Smacker video.
/// </summary>
public sealed class SmkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Smk";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Smacker Video";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".smk";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".smk"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SMK2"u8.ToArray(), Confidence: 0.95),
    new("SMK4"u8.ToArray(), Confidence: 0.95),
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
  public string Description => "Smacker video container (.smk); demux plus packet-preserving mux/remux over already encoded video and audio data.";

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

  /// <summary>
  /// Creates or remuxes a Smacker container from FULL.smk plus optional TRACKn.packets
  /// replacements, or from the lossless structural entries exposed by this descriptor.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => SmkWriter.Create(output, inputs, options);

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
