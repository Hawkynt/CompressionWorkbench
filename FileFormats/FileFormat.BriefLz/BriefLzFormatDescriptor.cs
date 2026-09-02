#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.BriefLz;

/// <summary>
/// Describes brief lz format.
/// </summary>
public sealed class BriefLzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BriefLz";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "BriefLZ";
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
public string DefaultExtension => ".blz";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".blz"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x62, 0x6C, 0x7A, 0x1A], Confidence: 0.90)];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("brieflz", "BriefLZ")];
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
public string Description => "Compact LZ77 with optimal parsing option";

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => BriefLzStream.Decompress(input, output);
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => BriefLzStream.Compress(input, output);
}
