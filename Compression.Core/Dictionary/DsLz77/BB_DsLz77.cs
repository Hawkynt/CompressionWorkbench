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
  public string Id => "BB_DsLz77";

  /// <inheritdoc/>
  public string DisplayName => "DS LZ77";

  /// <inheritdoc/>
  public string Description =>
    "Microsoft DoubleSpace/DriveSpace LZ77 (variable-bit length/distance, 4 KiB window)";

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => DsLz77Compressor.Compress(data, effort: 0);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => DsLz77Decompressor.Decompress(data);
}
