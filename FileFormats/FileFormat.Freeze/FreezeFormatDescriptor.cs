#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Freeze;

/// <summary>
/// Describes freeze format.
/// </summary>
public sealed class FreezeFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Freeze";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Freeze";
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
public string DefaultExtension => ".f";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".f", ".freeze"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1F, 0x9E], Confidence: 0.80)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("freeze", "Freeze")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZ77+Huffman, classic Unix compressor";

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => FreezeStream.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => FreezeStream.Compress(input, output);
}
