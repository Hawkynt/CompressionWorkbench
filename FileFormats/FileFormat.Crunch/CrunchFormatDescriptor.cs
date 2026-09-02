#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Crunch;

/// <summary>
/// Describes crunch format.
/// </summary>
public sealed class CrunchFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Crunch";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "CP/M Crunch";
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
public string DefaultExtension => ".cru";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".cru"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x76, 0xFE], Confidence: 0.85)];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("lzw", "LZW (9-12 bit)")];
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
public string Description => "CP/M Crunch, LZW 9-12 bit MSB-first with original filename header";

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) {
    using var ds = new CrunchStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }

    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) {
    using var cs = new CrunchStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }

    /// <summary>
  /// Performs the wrap decompress operation.
  /// </summary>
public Stream? WrapDecompress(Stream input) =>
    new CrunchStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);

    /// <summary>
  /// Performs the wrap compress operation.
  /// </summary>
public Stream? WrapCompress(Stream output) =>
    new CrunchStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
