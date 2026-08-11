using Compression.Registry;

namespace FileFormat.Rzip;

/// <summary>
/// Exposes rzip as a benchmarkable building block.
/// Produces a complete rzip stream — "RZIP" signature, version, the big-endian
/// original size and the token stream — so the payload carries its own length and
/// no extra uncompressed-size header is prepended.
/// </summary>
/// <remarks>
/// Two-stage design after Andrew Tridgell, "Efficient Algorithms for Sorting and
/// Synchronization" (PhD thesis, 1999), chapter 3: a rolling checksum indexes the
/// whole input so matches can reach arbitrarily far back, well past a classic
/// 32K/64K window, and the residual literals are then entropy coded. The concrete
/// layout, and how it differs from upstream rzip, is documented on
/// <see cref="RzipStream"/>.
/// </remarks>
public sealed class RzipBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Rzip";
  /// <inheritdoc/>
  public string DisplayName => "rzip";
  /// <inheritdoc/>
  public string Description => "Long-range rolling-hash match elimination over the whole input, with order-0 coded literals";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray());
    using var output = new MemoryStream();
    RzipStream.Compress(input, output);
    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray());
    using var output = new MemoryStream();
    RzipStream.Decompress(input, output);
    return output.ToArray();
  }
}
