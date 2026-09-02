#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zling;

/// <summary>
/// Describes zling format.
/// </summary>
public sealed class ZlingFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Zling";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Zling";
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
public string DefaultExtension => ".zling";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".zling"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "ROLZ + Huffman block compressor by Zhang Li";

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => ZlingStream.Decompress(input, output);
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => ZlingStream.Compress(input, output);
}
