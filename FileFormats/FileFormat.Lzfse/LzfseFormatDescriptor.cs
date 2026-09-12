#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzfse;

/// <summary>
/// Describes the Apple LZFSE compression stream format.
/// </summary>
public sealed class LzfseFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  public string Id => "Lzfse";
  public string DisplayName => "LZFSE";
  public FormatCategory Category => FormatCategory.Stream;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsOptimize;
  public string DefaultExtension => ".lzfse";
  public IReadOnlyList<string> Extensions => [".lzfse"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x62, 0x76, 0x78, 0x32], Confidence: 0.95), // bvx2
    new([0x62, 0x76, 0x78, 0x31], Confidence: 0.95), // bvx1
    new([0x62, 0x76, 0x78, 0x6E], Confidence: 0.90), // bvxn
    new([0x62, 0x76, 0x78, 0x2D], Confidence: 0.85), // bvx-
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzfse", "LZFSE", SupportsOptimize: true)];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  public string Description => "Apple LZFSE: LZ77 with FSE/tANS entropy coding plus LZVN and stored blocks";

  private static readonly IReadOnlyDictionary<string, int> BlockSizesByLabel =
    new Dictionary<string, int> {
      ["4 KB"] = 4 * 1024,
      ["8 KB"] = 8 * 1024,
      ["16 KB"] = 16 * 1024,
      ["30 KB"] = LzfseStream.DefaultBlockSize,
    };

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Mode",
      DisplayName: "Block encoder",
      Kind: FormatOptionKind.Enum,
      Default: nameof(LzfseBlockMode.Auto),
      AllowedValues: [nameof(LzfseBlockMode.Auto), nameof(LzfseBlockMode.Lzfse), nameof(LzfseBlockMode.Lzvn)],
      Description: "Auto chooses the smallest valid LZFSE/LZVN/stored representation independently for every block."),
    new FormatOptionDescriptor(
      Key: "Level",
      DisplayName: "Match-search depth",
      Kind: FormatOptionKind.Enum,
      Default: nameof(LzfseCompressionLevel.Balanced),
      AllowedValues: [nameof(LzfseCompressionLevel.Fast), nameof(LzfseCompressionLevel.Balanced), nameof(LzfseCompressionLevel.Maximum)],
      Description: "Controls how many LZFSE hash-chain candidates are tested at each input position."),
    new FormatOptionDescriptor(
      Key: "BlockSize",
      DisplayName: "Raw block size",
      Kind: FormatOptionKind.Enum,
      Default: "30 KB",
      AllowedValues: ["4 KB", "8 KB", "16 KB", "30 KB"],
      Description: "Raw bytes considered per independently optimized LZFSE block; smaller blocks adapt models faster but add headers."),
  ];

  public void Decompress(Stream input, Stream output) => LzfseStream.Decompress(input, output);

  public void Compress(Stream input, Stream output) => LzfseStream.Compress(input, output);

  public void Compress(Stream input, Stream output, FormatCreateOptions options) =>
    LzfseStream.Compress(input, output, ParseMode(options), ParseLevel(options), ParseBlockSize(options));

  public void CompressOptimal(Stream input, Stream output) =>
    LzfseStream.Compress(input, output, LzfseBlockMode.Auto, LzfseCompressionLevel.Maximum, LzfseStream.DefaultBlockSize);

  private static LzfseBlockMode ParseMode(FormatCreateOptions options) {
    var value = options.GetString("Mode");
    return Enum.TryParse<LzfseBlockMode>(value, ignoreCase: true, out var mode) ? mode : LzfseBlockMode.Auto;
  }

  private static LzfseCompressionLevel ParseLevel(FormatCreateOptions options) {
    var value = options.GetString("Level");
    return Enum.TryParse<LzfseCompressionLevel>(value, ignoreCase: true, out var level)
      ? level
      : LzfseCompressionLevel.Balanced;
  }

  private static int ParseBlockSize(FormatCreateOptions options) {
    var value = options.GetString("BlockSize");
    return value is not null && BlockSizesByLabel.TryGetValue(value, out var bytes)
      ? bytes
      : LzfseStream.DefaultBlockSize;
  }
}
