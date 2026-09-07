#pragma warning disable CS1591

using Compression.Registry;

namespace FileFormat.Lizard;

/// <summary>
/// Describes the Lizard (formerly LZ5) stream format.
/// </summary>
public sealed class LizardFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  private const int DefaultLevel = 17;
  private const int OptimalLevel = 19;
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
    "Lizard/LZ5 managed fast-LZ4 codeword family (levels 10-19) with interoperable Lizard block framing";

  /// <summary>
  /// Tunable Lizard parameters supported by the managed encoder. The upstream
  /// codec defines levels 10-49; levels 20-49 require LIZv1 and/or Huffman
  /// streams, so this schema intentionally exposes only interoperable levels
  /// 10-19 that this implementation can actually emit and decode.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Integer,
      Default: "17",
      AllowedValues: ["10", "11", "12", "13", "14", "15", "16", "17", "18", "19"],
      Description: "Lizard fast-LZ4 codeword level. 10 is fastest; 19 spends the most search effort."),
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
    return int.TryParse(raw, out var level) && level is >= 10 and <= 19 ? level : DefaultLevel;
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
