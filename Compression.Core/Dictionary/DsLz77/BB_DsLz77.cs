using Compression.Registry;

namespace Compression.Core.Dictionary.DsLz77;

/// <summary>
/// Benchmarkable building block for the DoubleSpace/DriveSpace LZ77 grammar.
/// Uses effort level 0 (greedy parse, 4 KiB window) so benchmark numbers are
/// the apples-to-apples baseline; CVF writers reach for the effort-1 / -2
/// variants directly via <see cref="DsLz77Compressor.Compress(ReadOnlySpan{byte}, int)"/>.
/// </summary>
public sealed class BB_DsLz77 : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_DsLz77";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DS LZ77";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Microsoft DoubleSpace/DriveSpace LZ77 (variable-bit length/distance, 4 KiB window)";

  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data)
    => DsLz77Compressor.Compress(data, effort: 0);

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data)
    => DsLz77Decompressor.Decompress(data);
}
