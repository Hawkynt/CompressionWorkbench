#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Paq8;

/// <summary>
/// Describes paq 8 format.
/// </summary>
public sealed class Paq8FormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Paq8";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PAQ8";
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
public string DefaultExtension => ".paq8l";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".paq8l", ".paq8"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x70, 0x61, 0x71, 0x38, 0x6C, 0x20, 0x2D], Confidence: 0.95)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [];
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
public string Description => "PAQ8 context-mixing compressor by Matt Mahoney";

  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => Paq8Stream.Compress(input, output);
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => Paq8Stream.Decompress(input, output);
}
