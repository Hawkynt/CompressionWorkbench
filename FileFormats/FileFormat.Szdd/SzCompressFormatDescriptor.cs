#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Szdd;

/// <summary>
/// The older "SZ " Microsoft COMPRESS variant (pre-SZDD; QBasic-era
/// <c>COMPRESS.EXE</c>). Magic <c>53 5A 20 88 F0 27 33 D1</c>, a 12-byte header
/// (8-byte magic + little-endian u32 uncompressed length) and a 4096-byte-ring
/// LZSS body. Neither the legacy SZDD reader nor 7-Zip handles this variant;
/// here it is fully read + write (<see cref="Compress(Stream,Stream)"/> emits
/// the "SZ " header, <see cref="Decompress"/> auto-detects either variant).
/// </summary>
public sealed class SzCompressFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "SzCompress";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "SZ (old MS COMPRESS)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Stream;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".sz";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x53, 0x5A, 0x20, 0x88, 0xF0, 0x27, 0x33, 0xD1], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzss", "LZSS", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Old Microsoft 'SZ ' COMPRESS LZSS (pre-SZDD / QBasic era), with exact parse optimization";

  /// <summary>The LZSS parsing strategy used when writing the SZ body.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(
      Key: "Parser",
      DisplayName: "Parse strategy",
      Kind: FormatOptionKind.Enum,
      Default: "Greedy",
      AllowedValues: ["Greedy", "Optimal"],
      Description: "Greedy keeps the fast legacy writer; Optimal uses exact dynamic parsing for the smallest SZ body."),
  ];

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => SzddStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input using the fast legacy parser.
  /// </summary>
  public void Compress(Stream input, Stream output) => SzddStream.CompressQBasic(input, output);
  /// <summary>
  /// Encodes the supplied input using the requested parse strategy.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    switch (options.GetOption("Parser", "Greedy")) {
      case "Greedy":
        SzddStream.CompressQBasic(input, output);
        break;
      case "Optimal":
        SzOptimizer.Compress(input, output);
        break;
      default:
        throw new ArgumentException("SZ Parser must be either 'Greedy' or 'Optimal'.", nameof(options));
    }
  }

  /// <summary>
  /// Encodes the supplied input with the exact size-optimal parser.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) => SzOptimizer.Compress(input, output);
}
