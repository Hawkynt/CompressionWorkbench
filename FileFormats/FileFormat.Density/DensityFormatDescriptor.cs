using Compression.Registry;

namespace FileFormat.Density;

/// <summary>
/// Describes density format.
/// </summary>
public sealed class DensityFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
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
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest;
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
    new("chameleon", "Chameleon"),
    new("cheetah", "Cheetah"),
    new("lion", "Lion"),
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
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => DensityStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => DensityStream.Compress(input, output);
}
