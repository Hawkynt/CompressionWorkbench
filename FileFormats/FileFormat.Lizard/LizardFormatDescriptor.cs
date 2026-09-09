#pragma warning disable CS1591

using Compression.Registry;

namespace FileFormat.Lizard;

/// <summary>
/// Describes the Lizard (formerly LZ5) stream format.
/// </summary>
public sealed class LizardFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  private const int DefaultLevel = 17;
  private const int OptimalLevel = 49;
  private const int DefaultBlockSize = 4 * 1024 * 1024;

  private static readonly IReadOnlyDictionary<string, int> BlockSizesByLabel =
    new Dictionary<string, int> {
      ["128 KB"] = 128 * 1024,
      ["256 KB"] = 256 * 1024,
      ["1 MB"] = 1024 * 1024,
      ["4 MB"] = DefaultBlockSize,
    };

  public string Id => "Lizard";
  public string DisplayName => "Lizard (LZ5)";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".liz";
  public IReadOnlyList<string> Extensions => [".liz"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x06, 0x22, 0x4D, 0x18], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lizard", "Lizard", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description =>
    "Lizard/LZ5 levels 10-49: fastLZ4, LIZv1, and their Huffman-coded variants with interoperable Lizard framing";

  /// <summary>
  /// Tunable Lizard parameters supported by the managed encoder. Upstream
  /// defines four method families across levels 10-49: fastLZ4 (10-19),
  /// LIZv1 (20-29), fastLZ4+HUF (30-39), and LIZv1+HUF (40-49).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Integer,
      Default: "17",
      AllowedValues: Enumerable.Range(10, 40).Select(static value => value.ToString()).ToArray(),
      Description: "Lizard compression level 10-49. Method family changes at 20, 30, and 40; higher levels spend more search effort."),
    new FormatOptionDescriptor(
      Key: "BlockSize",
      DisplayName: "Max frame block size",
      Kind: FormatOptionKind.Enum,
      Default: "4 MB",
      AllowedValues: ["128 KB", "256 KB", "1 MB", "4 MB"],
      Description: "Maximum Lizard frame block size. Smaller blocks reduce memory use and can alter compression ratio."),
  ];

  internal static int ParseLevel(FormatCreateOptions options) {
    var raw = options.GetString("Level");
    return int.TryParse(raw, out var level) && level is >= 10 and <= 49 ? level : DefaultLevel;
  }

  internal static int ParseBlockSize(FormatCreateOptions options) {
    var raw = options.GetString("BlockSize");
    return raw is not null && BlockSizesByLabel.TryGetValue(raw, out var blockSize)
      ? blockSize
      : DefaultBlockSize;
  }

  public void Decompress(Stream input, Stream output) => LizardStream.Decompress(input, output);

  public void Compress(Stream input, Stream output) =>
    LizardStream.Compress(input, output, DefaultLevel, DefaultBlockSize);

  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    LizardStream.Compress(input, output, ParseLevel(options), ParseBlockSize(options));

  public void CompressOptimal(Stream input, Stream output) =>
    LizardStream.Compress(input, output, OptimalLevel, DefaultBlockSize);
}
