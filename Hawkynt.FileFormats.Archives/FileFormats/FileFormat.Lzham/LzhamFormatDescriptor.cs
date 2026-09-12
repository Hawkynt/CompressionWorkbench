#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzham;
using Compression.Registry;

namespace FileFormat.Lzham;

/// <summary>
/// Describes lzham format.
/// </summary>
public sealed class LzhamFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Lzham";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZHAM";
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
  public string DefaultExtension => ".lzham";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".lzham"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4C, 0x5A, 0x48, 0x4D], Confidence: 0.90)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzham", "LZHAM", SupportsOptimize: true)];
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
  public string Description => "LZHAM container, LZ77 + Huffman (Valve-inspired codec)";

  /// <summary>
  /// Encoder-only match-finder tunables. Neither value changes the bitstream
  /// grammar, so every candidate is decoded by the same decoder. The generic
  /// compression optimizer can therefore exhaustively try all 24 combinations
  /// and keep the smallest result for the actual input.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "WindowSize",
      DisplayName: "Match window (bytes)",
      Kind: FormatOptionKind.Integer,
      Default: "32768",
      AllowedValues: ["4096", "8192", "16384", "32768"],
      Description: "Maximum backward distance considered for an LZ match. Smaller windows reduce search work; larger windows can find distant repetition."),
    new FormatOptionDescriptor(
      Key: "SearchDepth",
      DisplayName: "Match search depth",
      Kind: FormatOptionKind.Integer,
      Default: "64",
      AllowedValues: ["8", "16", "32", "64", "128", "256"],
      Description: "Maximum hash-chain candidates tested at each position. Deeper searches cost CPU and may find longer matches."),
  ];

  internal static int ParseWindowSize(FormatCreateOptions options) {
    var raw = options.GetString("WindowSize");
    return int.TryParse(raw, out var value) && value is 4096 or 8192 or 16384 or 32768
      ? value
      : LzhamEncoder.DefaultWindowSize;
  }

  internal static int ParseSearchDepth(FormatCreateOptions options) {
    var raw = options.GetString("SearchDepth");
    return int.TryParse(raw, out var value) && value is 8 or 16 or 32 or 64 or 128 or 256
      ? value
      : LzhamEncoder.DefaultSearchDepth;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => LzhamStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => LzhamStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using format-specific optimizer tunables.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => LzhamStream.Compress(input, output, ParseWindowSize(options), ParseSearchDepth(options));
  /// <summary>
  /// Encodes using the strongest built-in match finder. Schema-aware callers use
  /// the generic optimizer to compare every candidate on the actual payload.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output)
    => LzhamStream.Compress(input, output, LzhamEncoder.MaxWindowSize, searchDepth: 256);
}
