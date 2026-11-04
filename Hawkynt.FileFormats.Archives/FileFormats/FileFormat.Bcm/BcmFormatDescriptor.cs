#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Bcm;

/// <summary>
/// Describes the BCM 1.x format (<c>BCM!</c>), byte-compatible with BCM 1.65. BCM 2.x files
/// (<c>BCM2</c>) are refused: their format was never published.
/// </summary>
public sealed class BcmFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema, ICompressionOptimizable {
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
  public string Description => "Ilya Muravyov's BWT + context-mixing compressor, the BCM 1.x format (BCM 1.10 to 1.65)";

  /// <summary>
  /// bcm's one creation parameter, the block size (<c>-b#</c>, whole MiB). The default is bcm's
  /// own, 16 MiB; the optimizer may also try smaller blocks.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "BlockSize",
      DisplayName: "Block size (MiB)",
      Kind: FormatOptionKind.Enum,
      Default: "16",
      AllowedValues: ["1", "2", "4", "8", "16", "32", "64"],
      Description: "BWT block size in MiB, as bcm -b. Larger blocks expose more context and need about five times their size in memory."),
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
  /// Encodes the supplied input using the selected block size.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => BcmStream.Compress(input, output, ParseBlockSize(options));

  private static int ParseBlockSize(FormatCreateOptions options) {
    var raw = options.GetString("BlockSize");
    if (raw is null || !int.TryParse(raw, out var mebibytes))
      return BcmStream.DefaultBlockSize;

    return Math.Clamp(mebibytes, 1, 2047) << 20;
  }
}
