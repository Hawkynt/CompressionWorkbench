#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zling;

/// <summary>
/// Describes zling format.
/// </summary>
public sealed class ZlingFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Zling";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Zling";
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
  public string DefaultExtension => ".zling";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".zling"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zling", "Zling", SupportsOptimize: true)];
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
  public string Description => "ROLZ + Huffman block compressor by Zhang Li";

  // libzling exposes five effort levels (0..4). The managed encoder keeps its
  // historical deepest search as the default (4), while the optimizer tries all
  // five levels and keeps the smallest result for the caller's actual payload.
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Integer,
      Default: "4",
      AllowedValues: ["0", "1", "2", "3", "4"],
      Description: "ROLZ match-search effort from 0 (fastest) through 4 (deepest search)."),
  ];

  internal static int ParseLevel(FormatCreateOptions options) {
    var level = options.TryGetInt("Level", out var specific) ? specific : options.Level ?? 4;
    return Math.Clamp(level, 0, 4);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => ZlingStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => ZlingStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the selected compression level.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    ZlingStream.Compress(input, output, ParseLevel(options));
  /// <summary>
  /// Performs the compress optimal operation.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) => ZlingStream.Compress(input, output, 4);
}
