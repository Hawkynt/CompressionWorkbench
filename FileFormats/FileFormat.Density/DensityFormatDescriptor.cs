using Compression.Registry;

namespace FileFormat.Density;

/// <summary>
/// Describes density format.
/// </summary>
public sealed class DensityFormatDescriptor : IFormatDescriptor, IStreamFormatOperations, IFormatOptionsSchema {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Density";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Density";
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
  public string DefaultExtension => ".density";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".density"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'D', (byte)'E', (byte)'N', (byte)'S'], Confidence: 0.85)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("chameleon", "Chameleon", SupportsOptimize: true),
    new("cheetah", "Cheetah", SupportsOptimize: true),
    new("lion", "Lion", SupportsOptimize: true),
  ];
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
  public string Description => "Chameleon/Cheetah/Lion algorithms, tuned for speed tiers";

  /// <summary>
  /// Gets the finite Density algorithm axis searched by the generic stream optimizer.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Algorithm",
      DisplayName: "Compression algorithm",
      Kind: FormatOptionKind.Enum,
      Default: nameof(DensityStream.Algorithm.Cheetah),
      AllowedValues: [
        nameof(DensityStream.Algorithm.Chameleon),
        nameof(DensityStream.Algorithm.Cheetah),
        nameof(DensityStream.Algorithm.Lion),
      ],
      Description: "Density algorithm (Chameleon = fastest, Cheetah = balanced, Lion = best compression ratio)."),
  ];

  /// <summary>
  /// Resolves the requested algorithm, preserving Cheetah as the historical default.
  /// </summary>
  internal static DensityStream.Algorithm ParseAlgorithm(FormatCreateOptions options) {
    var raw = options.GetString("Algorithm");
    return Enum.TryParse<DensityStream.Algorithm>(raw, ignoreCase: true, out var algorithm)
      ? algorithm
      : DensityStream.Algorithm.Cheetah;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => DensityStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => DensityStream.Compress(input, output);
  /// <summary>
  /// Encodes the supplied input using the selected Density algorithm.
  /// </summary>
  public void Compress(Stream input, Stream output, FormatCreateOptions options)
    => DensityStream.Compress(input, output, ParseAlgorithm(options));
  /// <summary>
  /// Encodes using Density's ratio-oriented Lion tier.
  /// </summary>
  public void CompressOptimal(Stream input, Stream output)
    => DensityStream.Compress(input, output, DensityStream.Algorithm.Lion);
}
