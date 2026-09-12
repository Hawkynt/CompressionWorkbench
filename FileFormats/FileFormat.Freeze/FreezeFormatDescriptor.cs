#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Freeze;

/// <summary>
/// Describes interoperable Freeze 1.x and 2.x streams.
/// </summary>
public sealed class FreezeFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Freeze";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Freeze";
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
  public string DefaultExtension => ".f";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".f", ".freeze"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x1F, 0x9F], Confidence: 0.85),
    new([0x1F, 0x9E], Confidence: 0.75),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("freeze", "Freeze", SupportsOptimize: true)];
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
  public string Description => "Interoperable Freeze 1.x/2.x LZSS with adaptive and static Huffman coding";

  /// <summary>The finite Freeze encoder knobs searched by the generic compression optimizer.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: FormatOptionKeys.TargetCompatibility,
      DisplayName: "Target compatibility",
      Kind: FormatOptionKind.Enum,
      Default: nameof(FreezeCompatibility.Freeze2x),
      AllowedValues: [nameof(FreezeCompatibility.Freeze2x), nameof(FreezeCompatibility.Freeze1x)],
      Description: "Select the wire format to emit. Freeze1x targets the incompatible 1F 9E / 4 KiB / 60-byte-match generation; Freeze2x is the modern historical 1F 9F format.",
      IsOptimizationAxis: false),
    new FormatOptionDescriptor(
      Key: "Parsing",
      DisplayName: "Parse strategy",
      Kind: FormatOptionKind.Enum,
      Default: nameof(FreezeParsingStrategy.Lazy),
      AllowedValues: [nameof(FreezeParsingStrategy.Lazy), nameof(FreezeParsingStrategy.Greedy)],
      Description: "Lazy is Freeze's historical delayed parser; Greedy corresponds to freeze -g."),
    new FormatOptionDescriptor(
      Key: "SearchDepth",
      DisplayName: "Match search depth",
      Kind: FormatOptionKind.Integer,
      Default: FreezeCompressionOptions.DefaultSearchDepth.ToString(),
      AllowedValues: ["16", "32", "64", "128", "256", "512"],
      Description: "Maximum hash-chain candidates examined at each position; larger searches are slower but can find better matches."),
    new FormatOptionDescriptor(
      Key: "PositionTable",
      DisplayName: "Position Huffman table",
      Kind: FormatOptionKind.Enum,
      Default: nameof(FreezePositionTableMode.Default),
      AllowedValues: [nameof(FreezePositionTableMode.Default), nameof(FreezePositionTableMode.Optimized)],
      Description: "Freeze 2.x can carry a per-input position table, analogous to the historical statist workflow. Freeze 1.x has a fixed table.",
      DependsOn: $"{FormatOptionKeys.TargetCompatibility}={nameof(FreezeCompatibility.Freeze2x)}"),
  ];

  internal static FreezeCompressionOptions ParseOptions(FormatCreateOptions options) {
    var targetCompatibility = options.GetString(FormatOptionKeys.TargetCompatibility) is { } rawCompatibility
                              && Enum.TryParse<FreezeCompatibility>(rawCompatibility, ignoreCase: true, out var parsedCompatibility)
      ? parsedCompatibility
      : FreezeCompatibility.Freeze2x;
    var parsing = options.GetString("Parsing") is { } rawParsing
                  && Enum.TryParse<FreezeParsingStrategy>(rawParsing, ignoreCase: true, out var parsedParsing)
      ? parsedParsing
      : FreezeParsingStrategy.Lazy;
    var searchDepth = options.GetOptionInt("SearchDepth", FreezeCompressionOptions.DefaultSearchDepth);
    if (searchDepth <= 0)
      searchDepth = FreezeCompressionOptions.DefaultSearchDepth;
    var positionTable = options.GetString("PositionTable") is { } rawTable
                        && Enum.TryParse<FreezePositionTableMode>(rawTable, ignoreCase: true, out var parsedTable)
      ? parsedTable
      : FreezePositionTableMode.Default;
    return new FreezeCompressionOptions {
      TargetCompatibility = targetCompatibility,
      Parsing = parsing,
      SearchDepth = searchDepth,
      PositionTable = positionTable,
    };
  }

  /// <summary>
  /// Decodes the supplied input. The stream magic selects Freeze 1.x or 2.x internally.
  /// </summary>
  public void Decompress(Stream input, Stream output) => FreezeStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input using the default Freeze 2.x target.
  /// </summary>
  public void Compress(Stream input, Stream output) => FreezeStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input honoring compatibility and format-specific optimizer options.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    FreezeStream.Compress(input, output, ParseOptions(options));
  /// <summary>
  /// Compresses for the default Freeze 2.x target with the highest-effort built-in match search and a per-input position table.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    FreezeStream.Compress(input, output, new FreezeCompressionOptions {
      TargetCompatibility = FreezeCompatibility.Freeze2x,
      Parsing = FreezeParsingStrategy.Lazy,
      SearchDepth = 512,
      PositionTable = FreezePositionTableMode.Optimized,
    });
}
