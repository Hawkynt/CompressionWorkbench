#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Paq8;

/// <summary>
/// Describes paq 8 format.
/// </summary>
public sealed class Paq8FormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Paq8";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PAQ8";
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
  public string DefaultExtension => ".paq8l";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".paq8l", ".paq8"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x70, 0x61, 0x71, 0x38, 0x6C, 0x20, 0x2D], Confidence: 0.95)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "PAQ8 context-mixing compressor by Matt Mahoney";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────

  /// <summary>
  /// PAQ8L's documented compression levels are 1..8, with level 5 as the
  /// default. In this repository's simplified managed codec the level is stored
  /// in the stream and tunes predictor adaptation; the optimizer searches every
  /// supported level because the best learning rate depends on the input.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: "5",
      AllowedValues: ["1", "2", "3", "4", "5", "6", "7", "8"],
      Description: "PAQ8L level 1-8. Level 5 preserves the historical default; optimization searches all levels."),
  ];

  /// <summary>
  /// Resolves the PAQ8 level from the format-specific option first, then the
  /// generic compression level, falling back to the historical default (5).
  /// </summary>
  internal static int ParseLevel(FormatCreateOptions options) {
    var raw = options.GetString("Level");
    if (raw is not null && int.TryParse(raw, out var level))
      return Math.Clamp(level, Paq8Stream.MinLevel, Paq8Stream.MaxLevel);

    return Math.Clamp(options.Level ?? Paq8Stream.DefaultLevel, Paq8Stream.MinLevel, Paq8Stream.MaxLevel);
  }

  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => Paq8Stream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the selected compression level.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    Paq8Stream.Compress(input, output, ParseLevel(options));
  /// <summary>
  /// Encodes the supplied input at the highest PAQ8L level.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    Paq8Stream.Compress(input, output, Paq8Stream.MaxLevel);
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => Paq8Stream.Decompress(input, output);
}
