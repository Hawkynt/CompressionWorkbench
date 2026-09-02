#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Lzfse;

/// <summary>
/// Describes lzfse format.
/// </summary>
public sealed class LzfseFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Lzfse";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZFSE";
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
public string DefaultExtension => ".lzfse";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".lzfse"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x62, 0x76, 0x78, 0x31], Confidence: 0.85)];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("lzfse", "LZFSE")];
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
public string Description => "Apple's entropy-coded LZ77 for iOS/macOS";

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => LzfseStream.Decompress(input, output);
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => LzfseStream.Compress(input, output);
}
