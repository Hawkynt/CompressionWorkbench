#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzham;

/// <summary>
/// Describes lzham format.
/// </summary>
public sealed class LzhamFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema, ICompressionOptimizable {
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
    new([0x4C, 0x5A, 0x48, 0x30], Confidence: 0.90)
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
  public string Description => "LZHAM (Rich Geldreich) in the LZH0 file layout of LZHAM's lzhamtest tool";

  /// <summary>
  /// The dictionary size is the one LZHAM stream parameter the file records; "auto" picks the
  /// smallest that covers the input. The table update rate is not recorded by the file layout, so
  /// files are always written with LZHAM's default. Level and extreme parsing choose the parse; with
  /// the same dictionary size the bytes are those of <c>lzhamtest -t0 -m# [-x]</c>.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "DictionarySize",
      DisplayName: "Dictionary size (log2)",
      Kind: FormatOptionKind.Enum,
      Default: "auto",
      AllowedValues: ["auto", "15", "16", "18", "20", "22", "24", "26"],
      Description: "log2 of the LZHAM dictionary size, as lzhamtest -d. Larger dictionaries reach farther back but enlarge the match-slot alphabet."),
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Level",
      Kind: FormatOptionKind.Enum,
      Default: "4",
      AllowedValues: ["0", "1", "2", "3", "4"],
      Description: "LZHAM compression level, as lzhamtest -m: 0 fastest .. 4 uber (lzhamtest's default).",
      IsOptimizationAxis: false),
    new FormatOptionDescriptor(
      Key: "ExtremeParsing",
      DisplayName: "Extreme parsing",
      Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Keep four candidate parses per byte at level 4, as lzhamtest -x. Several times slower for a few percent."),
  ];

  internal static LzhamCompressionLevel ParseLevel(FormatCreateOptions options)
    => options.TryGetInt("Level", out var value) && value is >= 0 and <= 4 ? (LzhamCompressionLevel)value : LzhamCompressionLevel.Uber;

  internal static int ParseDictionarySize(FormatCreateOptions options)
    => int.TryParse(options.GetString("DictionarySize"), out var value) && value is >= 15 and <= 29 ? value : 0;

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => LzhamStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => LzhamStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input with the selected dictionary size.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => LzhamStream.Compress(input, output, ParseDictionarySize(options), LzhamTables.DefaultTableUpdateRate,
      ParseLevel(options), options.GetOptionBool("ExtremeParsing", false));
}
