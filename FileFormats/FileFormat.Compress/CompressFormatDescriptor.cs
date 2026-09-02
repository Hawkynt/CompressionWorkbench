#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Compress;

/// <summary>
/// Describes compress format.
/// </summary>
public sealed class CompressFormatDescriptor : IFormatDescriptor, IStreamFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Compress";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Unix Compress";
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
public string DefaultExtension => ".z";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".z"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x1F, 0x9D], Confidence: 0.85)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("lzw", "LZW", SupportsOptimize: true)];
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
public string Description => "Unix compress, LZW adaptive dictionary";

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Decompress(Stream input, Stream output) {
    using var ds = new CompressStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(output);
  }
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public void Compress(Stream input, Stream output) {
    using var cs = new CompressStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    input.CopyTo(cs);
  }
  /// <summary>
  /// Performs the wrap decompress operation.
  /// </summary>
public Stream? WrapDecompress(Stream input) =>
    new CompressStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
  /// <summary>
  /// Performs the wrap compress operation.
  /// </summary>
public Stream? WrapCompress(Stream output) =>
    new CompressStream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
}
