#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzs;
using Compression.Registry;

namespace FileFormat.Lzs;

/// <summary>
/// Describes lzs format.
/// </summary>
public sealed class LzsFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Lzs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZS";
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
  public string DefaultExtension => ".lzs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".lzs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x1F, 0x9D, 0x8C, 0x53], Confidence: 0.90)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzs", "LZS", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Stac LZS (RFC 1967/2395), LZSS variant for networking";

  /// <summary>The finite parsing-effort axis searched by <c>CompressionOptimizer</c>.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: nameof(LzsCompressionLevel.Balanced),
      AllowedValues: [
        nameof(LzsCompressionLevel.Fast),
        nameof(LzsCompressionLevel.Balanced),
        nameof(LzsCompressionLevel.Maximum),
      ],
      Description: "Fast limits match search, Balanced is the default, Maximum scans the full 2047-byte history and enables lazy parsing."),
  ];

  /// <summary>Decodes the supplied input.</summary>
  public void Decompress(Stream input, Stream output) => LzsStream.Decompress(input, output);

  /// <summary>Encodes the supplied input with the balanced encoder.</summary>
  public void Compress(Stream input, Stream output) => LzsStream.Compress(input, output);

  /// <summary>Encodes the supplied input honoring the format-specific compression level.</summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => LzsStream.Compress(input, output, ParseLevel(options));

  /// <summary>Tries every available LZS parsing effort and writes the smallest result.</summary>
  public void CompressOptimal(Stream input, Stream output) => LzsStream.CompressOptimal(input, output);

  private static LzsCompressionLevel ParseLevel(FormatCreateOptions options) {
    var raw = options.GetString("Level");
    return Enum.TryParse<LzsCompressionLevel>(raw, ignoreCase: true, out var level)
      ? level
      : LzsCompressionLevel.Balanced;
  }
}
