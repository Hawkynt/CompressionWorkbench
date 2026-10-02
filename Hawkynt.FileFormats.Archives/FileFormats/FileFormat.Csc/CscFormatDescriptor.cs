#pragma warning disable CS1591
using Compression.Core.Dictionary.Csc;
using Compression.Registry;

namespace FileFormat.Csc;

/// <summary>
/// Describes the libcsc stream format (Fu Siyuan's CSC), as written by the reference <c>csc</c> tool.
/// </summary>
public sealed class CscFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema, ICompressionOptimizable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Csc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "CSC";
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
  public string DefaultExtension => ".csc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".csc"];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("csc", "CSC", SupportsOptimize: true)];
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
  public string Description => "libcsc stream by Fu Siyuan: LZMA-inspired LZ77 with range coding, optimal parsing and per-block filters";

  /// <summary>
  /// The reference <c>csc</c> tool's own switches: <c>-m1</c>..<c>-m5</c> select the parser and match
  /// finder (levels 1-2 lazy with hash chains, 3-4 optimal, 5 optimal with a binary tree) and
  /// <c>-d</c> requests a window, which the encoder shrinks to the input length anyway, so the window
  /// is left out of the optimizer's search.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Integer,
      Default: CscEncoderOptions.DefaultLevel.ToString(),
      AllowedValues: ["1", "2", "3", "4", "5"],
      Description: "libcsc effort from fastest (1) to strongest (5); the reference tool defaults to 2."),
    new FormatOptionDescriptor(
      Key: "DictionarySize",
      DisplayName: "Dictionary size (bytes)",
      Kind: FormatOptionKind.Integer,
      Default: CscEncoderOptions.DefaultDictionarySize.ToString(),
      Description: "Requested LZ77 window, 32768 to 1073741823 bytes; shrunk to the input length like the reference tool does.",
      IsOptimizationAxis: false),
  ];

  internal static CscEncoderOptions ParseOptions(FormatCreateOptions options) {
    var level = options.TryGetInt("Level", out var requested) ? requested : options.Level ?? CscEncoderOptions.DefaultLevel;
    long dictionary = options.TryGetInt("DictionarySize", out var size) ? size
      : options.DictSize > 0 ? options.DictSize
      : CscEncoderOptions.DefaultDictionarySize;
    return new(
      Math.Clamp(level, 1, 5),
      Math.Clamp(dictionary, CscEncoderOptions.MinDictionarySize, CscEncoderOptions.MaxDictionarySize));
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => CscStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input with the reference tool's defaults.
  /// </summary>
  public void Compress(Stream input, Stream output) => CscStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the requested CSC tuning options.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    CscStream.Compress(input, output, ParseOptions(options));
  /// <summary>
  /// Encodes the supplied input at the strongest level.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    CscStream.Compress(input, output, new CscEncoderOptions(Level: 5));
}
