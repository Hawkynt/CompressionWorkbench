#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Rnc;

/// <summary>Describes the RNC ProPack stream format.</summary>
public sealed class RncFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  private static readonly IReadOnlyDictionary<string, int> DictionarySizes = new Dictionary<string, int> {
    ["4 KB"] = 4 << 10,
    ["16 KB"] = 16 << 10,
    ["32 KB"] = 32 << 10,
  };

  private static readonly IReadOnlyDictionary<string, int> BlockSizes = new Dictionary<string, int> {
    ["4 KB"] = 4 << 10,
    ["12 KB"] = 12 << 10,
    ["24 KB"] = 24 << 10,
    ["32 KB"] = 0x7FFF,
  };

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Rnc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "RNC ProPack";
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
  public string DefaultExtension => ".rnc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".rnc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x52, 0x4E, 0x43, 0x01], Confidence: 0.90)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rnc", "RNC Method 1", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Rob Northen Computing ProPack Method 1 — canonical Huffman-coded LZ77";

  /// <summary>
  /// The encoder axes searched by <c>CompressionOptimizer</c>.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(
      Key: "DictionarySize",
      DisplayName: "Dictionary size",
      Kind: FormatOptionKind.Enum,
      Default: "32 KB",
      AllowedValues: ["4 KB", "16 KB", "32 KB"],
      Description: "Maximum backward match distance. ProPack Method 1 supports up to 32 KiB; some games were traditionally packed with 16 KiB."),
    new(
      Key: "BlockSize",
      DisplayName: "Huffman block size",
      Kind: FormatOptionKind.Enum,
      Default: "12 KB",
      AllowedValues: ["4 KB", "12 KB", "24 KB", "32 KB"],
      Description: "Maximum uncompressed bytes sharing one set of three Huffman tables. ProPack's default is 12 KiB."),
    new(
      Key: "SearchDepth",
      DisplayName: "Match search depth",
      Kind: FormatOptionKind.Integer,
      Default: "256",
      AllowedValues: ["64", "256", "1024", "4096"],
      Description: "Hash-chain candidates examined per position. Larger values trade compression time for additional match choices."),
    new(
      Key: "Parser",
      DisplayName: "LZ parser",
      Kind: FormatOptionKind.Enum,
      Default: "Lazy",
      AllowedValues: ["Greedy", "Lazy"],
      Description: "Lazy performs ProPack-style one-byte look-ahead; greedy emits the current longest match immediately."),
  ];

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => RncStream.Decompress(input, output);

  /// <summary>
  /// Encodes the supplied input with the ProPack default settings.
  /// </summary>
  public void Compress(Stream input, Stream output) => RncStream.Compress(input, output);

  /// <summary>
  /// Encodes the supplied input with the requested dictionary, block size, search depth and parser.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => RncStream.Compress(input, output, ParseOptions(options));

  /// <summary>
  /// Encodes the supplied input with the widest dictionary and the deepest search this codec offers.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output)
    => RncStream.Compress(input, output, new RncCompressionOptions {
      DictionarySize = 0x8000,
      BlockSize = 0x3000,
      SearchDepth = 4096,
      ParseStrategy = RncParseStrategy.Lazy,
    });

  internal static RncCompressionOptions ParseOptions(FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var dictionarySize = DictionarySizes.TryGetValue(options.GetOption("DictionarySize", "32 KB"), out var dictionary)
      ? dictionary
      : 0x8000;
    var blockSize = BlockSizes.TryGetValue(options.GetOption("BlockSize", "12 KB"), out var block)
      ? block
      : 0x3000;
    var searchDepth = Math.Clamp(options.GetOptionInt("SearchDepth", 256), 1, 4096);
    var parser = Enum.TryParse<RncParseStrategy>(options.GetOption("Parser", "Lazy"), ignoreCase: true, out var parsed)
      ? parsed
      : RncParseStrategy.Lazy;

    return new RncCompressionOptions {
      DictionarySize = dictionarySize,
      BlockSize = blockSize,
      SearchDepth = searchDepth,
      ParseStrategy = parser,
    };
  }
}
