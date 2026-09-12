#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Bcm;

/// <summary>
/// Describes bcm format.
/// </summary>
public sealed class BcmFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  private const int KiB = 1024;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Bcm";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "BCM";
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
  public string DefaultExtension => ".bcm";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".bcm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x42, 0x43, 0x4D, 0x21], Confidence: 0.95)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bcm", "BCM", SupportsOptimize: true)];
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
  public string Description => "Ilya Muravyov's BWT + Context Mixing compressor";

  /// <summary>
  /// Finite BWT block-size search space for the generic compression optimizer.
  /// The default remains 64 KiB for byte-compatible behaviour with streams
  /// produced by previous versions. The search is intentionally capped at
  /// 128 KiB because the current managed rotation sort becomes expensive as the
  /// block grows; callers can still use the lower-level stream overload directly.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "BlockSize",
      DisplayName: "BWT block size (KiB)",
      Kind: FormatOptionKind.Enum,
      Default: "64",
      AllowedValues: ["16", "32", "64", "128"],
      Description: "BWT block size in KiB. Larger blocks expose more context; smaller blocks reduce transform cost and can sometimes encode better."),
  ];

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => BcmStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => BcmStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the selected BWT block size.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => BcmStream.Compress(input, output, ParseBlockSize(options));

  private static int ParseBlockSize(FormatCreateOptions options) {
    var raw = options.GetString("BlockSize");
    if (raw is null || !int.TryParse(raw, out var blockSizeKiB))
      return BcmStream.DefaultBlockSize;

    blockSizeKiB = Math.Clamp(blockSizeKiB, 16, BcmStream.MaximumOptimizedBlockSize / KiB);
    return blockSizeKiB * KiB;
  }
}
