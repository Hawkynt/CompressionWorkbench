#pragma warning disable CS1591

using Compression.Registry;

namespace FileFormat.QuickLz;

/// <summary>
/// Describes quick lz format.
/// </summary>
public sealed class QuickLzFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "QuickLz";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "QuickLZ";
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
public string DefaultExtension => ".quicklz";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".quicklz"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // QuickLZ has no reliable magic bytes (just a flags byte with bit 6 set) — detect by extension only.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("level1", "Level 1")];
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
public string Description => "Fast LZ77 compressor by Lasse Mikkel Reinhold";

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) => QuickLzStream.Decompress(input, output);
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) => QuickLzStream.Compress(input, output);
}
