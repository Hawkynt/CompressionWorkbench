#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Csc;

/// <summary>
/// Describes csc format.
/// </summary>
public sealed class CscFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  private const int DefaultLevel = 4;
  private const int MinDictionarySize = 32 * 1024;
  private const int MaxDictionarySize = 64 * 1024;

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
  public string Description => "Context Stream Compression by Fu Siyuan; LZ77 with range coding";

  /// <summary>
  /// CSC's original encoder exposes five effort levels and a dictionary-size knob.
  /// This reduced managed encoder keeps the same useful tuning dimensions: level
  /// controls hash-chain search depth, while dictionary size controls the LZ77
  /// look-back window. The 16-bit distance representation limits this stream
  /// implementation to 64 KiB dictionaries.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Integer,
      Default: DefaultLevel.ToString(),
      AllowedValues: ["1", "2", "3", "4", "5"],
      Description: "Search effort from fastest (1) to strongest (5); higher levels inspect more LZ77 candidates."),
    new FormatOptionDescriptor(
      Key: "DictionarySize",
      DisplayName: "Dictionary size (bytes)",
      Kind: FormatOptionKind.Integer,
      Default: MaxDictionarySize.ToString(),
      AllowedValues: ["32768", "65536"],
      Description: "LZ77 look-back window. This managed CSC stream supports the 32 KiB and 64 KiB presets."),
  ];

  internal static int ParseLevel(FormatCreateOptions options) {
    var level = options.TryGetInt("Level", out var requested) ? requested : options.Level ?? DefaultLevel;
    return Math.Clamp(level, 1, 5);
  }

  internal static int ParseDictionarySize(FormatCreateOptions options) {
    if (options.TryGetInt("DictionarySize", out var requested))
      return Math.Clamp(requested, MinDictionarySize, MaxDictionarySize);

    if (options.DictSize > 0)
      return (int)Math.Clamp(options.DictSize, MinDictionarySize, MaxDictionarySize);

    return MaxDictionarySize;
  }

  internal static int SearchDepthForLevel(int level) => Math.Clamp(level, 1, 5) switch {
    1 => 8,
    2 => 16,
    3 => 32,
    4 => 64,
    _ => 256,
  };

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => CscStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => CscStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the requested CSC tuning options.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options) {
    var level = ParseLevel(options);
    CscStream.Compress(input, output, ParseDictionarySize(options), SearchDepthForLevel(level));
  }
  /// <summary>
  /// Encodes the supplied input using the strongest supported single CSC preset.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output) =>
    CscStream.Compress(input, output, MaxDictionarySize, SearchDepthForLevel(5));
}
