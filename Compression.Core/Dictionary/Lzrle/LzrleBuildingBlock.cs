using Compression.Registry;

namespace Compression.Core.Dictionary.Lzrle;

/// <summary>
/// Exposes LZRLE (run-length-augmented LZ) as a benchmarkable building block.
/// </summary>
/// <remarks>
/// See <see cref="LzrleConstants"/> for the format layout and provenance notes.
/// </remarks>
public sealed class LzrleBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lzrle";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZRLE";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZ77 dictionary compression augmented with a dedicated run-length token for repeated bytes";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) =>
    LzrleCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) =>
    LzrleDecompressor.Decompress(data);
}
