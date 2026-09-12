#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Squeeze;

/// <summary>
/// Describes squeeze format.
/// </summary>
public sealed class SqueezeFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Squeeze";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Squeeze";
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
  public string DefaultExtension => ".sqz";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".sqz"];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("squeeze", "Squeeze", SupportsOptimize: true)];
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
  public string Description => "CP/M era RLE + static Huffman squeezing (Richard Greenlaw, 1981)";

  /// <summary>
  /// Squeeze has one useful encoding freedom: a repeated byte does not have to be represented by
  /// an RLE token. The optimizer exhaustively searches every threshold from the historical 3-byte
  /// rule through 256 (repeat tokens disabled) and lets the resulting Huffman tree decide which is smallest.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "RleMinimumRunLength",
      DisplayName: "Minimum RLE run length",
      Kind: FormatOptionKind.Integer,
      Default: "3",
      AllowedValues: Enumerable.Range(3, 254)
        .Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .ToArray(),
      Description: "Smallest repeated-byte run encoded as value/0x90/count. 3 is the historical SQ rule; 256 suppresses repeat tokens while literal 0x90 bytes remain escaped."),
  ];

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => SqueezeStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => SqueezeStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the requested RLE threshold.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    SqueezeStream.Compress(input, output, options.GetOptionInt("RleMinimumRunLength", 3));
  }
}
