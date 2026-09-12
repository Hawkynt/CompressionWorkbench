#pragma warning disable CS1591

using Compression.Core.Dictionary.QuickLz;
using Compression.Registry;

namespace FileFormat.QuickLz;

/// <summary>
/// Describes quick lz format.
/// </summary>
public sealed class QuickLzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "QuickLz";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "QuickLZ";
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
  public string DefaultExtension => ".quicklz";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".quicklz"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // QuickLZ has no reliable magic bytes (just a flags byte with bit 6 set) — detect by extension only.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("level1", "Level 1"),
    new("level3", "Level 3", SupportsOptimize: true),
  ];
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
  public string Description => "Fast LZ77 compressor by Lasse Mikkel Reinhold";

  /// <summary>The QuickLZ encoder knobs searched by the generic compression optimizer.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: nameof(QuickLzCompressionLevel.Level1),
      AllowedValues: [nameof(QuickLzCompressionLevel.Level1), nameof(QuickLzCompressionLevel.Level3)],
      Description: "Level 1 favors compression speed; level 3 searches more matches and targets the best ratio."),
    new FormatOptionDescriptor(
      Key: "SearchDepth",
      DisplayName: "Level 3 search depth",
      Kind: FormatOptionKind.Integer,
      Default: QuickLzCompressor.Level3MaxSearchDepth.ToString(),
      AllowedValues: ["1", "2", "4", "8", "16"],
      Description: "Recent hash candidates examined per position. 16 is the QuickLZ 1.5.0 best-ratio reference setting; lower values compress faster.",
      DependsOn: $"Level={nameof(QuickLzCompressionLevel.Level3)}"),
  ];

  /// <summary>Parses the QuickLZ compression level, preserving level 1 as the historical default.</summary>
  internal static QuickLzCompressionLevel ParseLevel(FormatCreateOptions options) {
    var raw = options.GetString("Level");
    return Enum.TryParse<QuickLzCompressionLevel>(raw, ignoreCase: true, out var level) &&
           level is QuickLzCompressionLevel.Level1 or QuickLzCompressionLevel.Level3
      ? level
      : QuickLzCompressionLevel.Level1;
  }

  /// <summary>Parses the level-3 match-search depth, defaulting to the reference depth of 16.</summary>
  internal static int ParseSearchDepth(FormatCreateOptions options) {
    var raw = options.GetString("SearchDepth");
    return int.TryParse(raw, out var depth) && depth is >= 1 and <= QuickLzCompressor.Level3MaxSearchDepth
      ? depth
      : QuickLzCompressor.Level3MaxSearchDepth;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => QuickLzStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => QuickLzStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using format-specific optimizer parameters.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    QuickLzStream.Compress(input, output, ParseLevel(options), ParseSearchDepth(options));
  /// <summary>
  /// Tries the supported QuickLZ encoder configurations and writes the smallest packet.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) => QuickLzStream.CompressOptimal(input, output);
}
