#pragma warning disable CS1591
using Compression.Core.Dictionary.Lzma;
using Compression.Registry;

namespace FileFormat.Lzma;

/// <summary>
/// Describes lzma format.
/// </summary>
public sealed class LzmaFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Lzma";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZMA";
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
public string DefaultExtension => ".lzma";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".lzma"];
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
public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma", "LZMA", SupportsOptimize: true)];
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
public string Description => "LZMA range-coded LZ77 with large dictionaries, high compression";

  // ── IFormatOptionsSchema ───────────────────────────────────────────────
  // The LZMA-alone header stores the dictionary size and the lc/lp/pb
  // properties byte, so every combination below decodes self-describingly.
  // The optimizer searches these axes to find the smallest output for the
  // caller's data (exhaustive under its budget, else coordinate descent).

  /// <summary>Maps each <c>DictionarySize</c> dropdown label to its size in bytes.</summary>
  private static readonly IReadOnlyDictionary<string, int> DictionarySizesByLabel =
    new Dictionary<string, int> {
      ["64 KB"] = 1 << 16,
      ["256 KB"] = 1 << 18,
      ["1 MB"] = 1 << 20,
      ["4 MB"] = 1 << 22,
      ["8 MB"] = 1 << 23,
      ["16 MB"] = 1 << 24,
      ["64 MB"] = 1 << 26,
    };

  /// <summary>The tunable LZMA knobs: compression level, dictionary size and the
  /// lc/lp/pb literal/position modelling bits.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: "Normal",
      AllowedValues: ["Fast", "Normal", "Best"],
      Description: "Effort spent searching for matches (Fast = quickest, Best = smallest)."),
    new FormatOptionDescriptor(
      Key: "DictionarySize",
      DisplayName: "Dictionary size",
      Kind: FormatOptionKind.Enum,
      Default: "8 MB",
      AllowedValues: ["64 KB", "256 KB", "1 MB", "4 MB", "8 MB", "16 MB", "64 MB"],
      Description: "Sliding-window size; larger finds longer-range matches at higher memory cost."),
    new FormatOptionDescriptor(
      Key: "Lc",
      DisplayName: "Literal context bits (lc)",
      Kind: FormatOptionKind.Integer,
      Default: "3",
      AllowedValues: ["0", "1", "2", "3", "4"],
      Description: "High bits of the previous byte used to model the next literal."),
    new FormatOptionDescriptor(
      Key: "Lp",
      DisplayName: "Literal position bits (lp)",
      Kind: FormatOptionKind.Integer,
      Default: "0",
      AllowedValues: ["0", "1", "2"],
      Description: "Position bits used to model literals (helps fixed-period data)."),
    new FormatOptionDescriptor(
      Key: "Pb",
      DisplayName: "Position bits (pb)",
      Kind: FormatOptionKind.Integer,
      Default: "2",
      AllowedValues: ["0", "1", "2"],
      Description: "Position bits used to model match positions."),
  ];

  /// <summary>Parses the compression level from the options, falling back to Normal.</summary>
  internal static LzmaCompressionLevel ParseLevel(FormatCreateOptions options) {
    var raw = options.FormatSpecific?.GetValueOrDefault("Level");
    return raw is not null && Enum.TryParse<LzmaCompressionLevel>(raw, ignoreCase: true, out var level)
      ? level
      : LzmaCompressionLevel.Normal;
  }

  /// <summary>Parses the dictionary size label into bytes, falling back to the 8 MiB default.</summary>
  internal static int ParseDictionarySize(FormatCreateOptions options) {
    var raw = options.FormatSpecific?.GetValueOrDefault("DictionarySize");
    return raw is not null && DictionarySizesByLabel.TryGetValue(raw, out var bytes)
      ? bytes
      : LzmaConstants.DefaultDictionarySize;
  }

  /// <summary>Parses an integer knob, clamping it to <paramref name="min"/>..<paramref name="max"/>
  /// and falling back to <paramref name="fallback"/> when absent or unparseable.</summary>
  private static int ParseClampedInt(FormatCreateOptions options, string key, int min, int max, int fallback) {
    var raw = options.FormatSpecific?.GetValueOrDefault(key);
    return raw is not null && int.TryParse(raw, out var value)
      ? Math.Clamp(value, min, max)
      : fallback;
  }

  /// <summary>Parses the literal context bits (lc), clamped to the valid 0..8 range.</summary>
  internal static int ParseLc(FormatCreateOptions options) => ParseClampedInt(options, "Lc", 0, 8, 3);

  /// <summary>Parses the literal position bits (lp), clamped to the valid 0..4 range.</summary>
  internal static int ParseLp(FormatCreateOptions options) => ParseClampedInt(options, "Lp", 0, 4, 0);

  /// <summary>Parses the position bits (pb), clamped to the valid 0..4 range.</summary>
  internal static int ParsePb(FormatCreateOptions options) => ParseClampedInt(options, "Pb", 0, 4, 2);

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => LzmaStream.Decompress(input, output);

  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => LzmaStream.Compress(input, output);

  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    LzmaStream.Compress(
      input, output,
      ParseDictionarySize(options),
      ParseLc(options), ParseLp(options), ParsePb(options),
      ParseLevel(options));

  /// <summary>
  /// Performs the compress optimal operation.
  /// </summary>
public void CompressOptimal(Stream input, Stream output) =>
    LzmaStream.Compress(input, output, dictionarySize: 1 << 24, lc: 3, lp: 0, pb: 2, level: LzmaCompressionLevel.Best);
}
