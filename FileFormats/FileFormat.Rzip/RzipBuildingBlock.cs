using Compression.Registry;

namespace FileFormat.Rzip;

/// <summary>
/// Exposes rzip as a benchmarkable building block.
/// Produces a complete rzip stream — "RZIP" signature, version, the big-endian
/// original size and the bzip2-compressed token chunks — so the payload is
/// self-terminating and no extra uncompressed-size header is prepended.
/// </summary>
/// <remarks>
/// Two-stage design after Andrew Tridgell, "Efficient Algorithms for Sorting and
/// Synchronization" (PhD thesis, 1999), chapter 3: a rolling checksum indexes the
/// whole input so LZ77-style matches can reach arbitrarily far back, well past a
/// classic 32K/64K window, and the residual literal/match token stream is then
/// handed to bzip2. The concrete on-disk layout is documented on
/// <see cref="RzipStream"/>.
/// </remarks>
public sealed class RzipBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Rzip";
  /// <inheritdoc/>
  public string DisplayName => "rzip";
  /// <inheritdoc/>
  public string Description => "Long-range rolling-hash match elimination over the whole input, followed by bzip2";
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
