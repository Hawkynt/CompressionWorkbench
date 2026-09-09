#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Bsc;

/// <summary>
/// Describes bsc format.
/// </summary>
public sealed class BscFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Bsc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "BSC";
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
  public string DefaultExtension => ".bsc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".bsc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x62, 0x73, 0x63, 0x31], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bsc", "BSC", SupportsOptimize: true)];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Ilya Grebnov's libbsc block sorting compressor (BWT+QLFC with optional LZP)";

  /// <summary>
  /// Searchable BSC creation parameters. The defaults and coder identifiers map
  /// to libbsc's command-line defaults and LIBBSC_CODER_QLFC_* constants.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(
      Key: "BlockSize",
      DisplayName: "Block size (bytes)",
      Kind: FormatOptionKind.Integer,
      Default: "26214400",
      AllowedValues: ["16384", "65536", "262144", "1048576", "4194304", "26214400"],
      Description: "Uncompressed bytes per BSC block. libbsc defaults to 25 MiB; smaller blocks can win on heterogeneous inputs."),
    new(
      Key: "SortingContexts",
      DisplayName: "Sorting contexts",
      Kind: FormatOptionKind.Enum,
      Default: "Following",
      AllowedValues: ["Following", "Preceding"],
      Description: "Context direction for block sorting. Preceding reverses each block before BWT and reverses it back after decode, matching libbsc -cp."),
    new(
      Key: "EntropyCoder",
      DisplayName: "QLFC entropy coder",
      Kind: FormatOptionKind.Enum,
      Default: "Static",
      AllowedValues: ["Fast", "Static", "Adaptive"],
      Description: "libbsc QLFC coder: fast (-c0), static/default (-c1), or adaptive (-c2)."),
  ];

  internal static int ParseBlockSize(FormatCreateOptions options)
    => options.TryGetInt("BlockSize", out var blockSize)
      ? Math.Clamp(blockSize, BscStream.MinimumBlockSize, BscStream.MaximumBlockSize)
      : BscStream.DefaultBlockSize;

  internal static BscSortingContexts ParseSortingContexts(FormatCreateOptions options)
    => options.GetString("SortingContexts")?.Equals("Preceding", StringComparison.OrdinalIgnoreCase) == true
      ? BscSortingContexts.Preceding
      : BscSortingContexts.Following;

  internal static BscEntropyCoder ParseEntropyCoder(FormatCreateOptions options)
    => options.GetString("EntropyCoder")?.ToUpperInvariant() switch {
      "FAST" => BscEntropyCoder.Fast,
      "ADAPTIVE" => BscEntropyCoder.Adaptive,
      _ => BscEntropyCoder.Static,
    };

  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => BscStream.Compress(input, output);

  /// <summary>
  /// Encodes the supplied input using the selected optimizer parameters.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => BscStream.Compress(
      input,
      output,
      ParseBlockSize(options),
      ParseSortingContexts(options),
      ParseEntropyCoder(options));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => BscStream.Decompress(input, output);
}
