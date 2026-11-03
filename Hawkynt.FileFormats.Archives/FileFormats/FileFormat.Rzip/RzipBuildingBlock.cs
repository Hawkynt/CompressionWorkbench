using Compression.Registry;

namespace FileFormat.Rzip;

/// <summary>
/// Exposes rzip as a benchmarkable building block. Produces a complete rzip 2.x file
/// (see <see cref="RzipStream"/>), which carries its own length, so no extra
/// uncompressed-size header is prepended.
/// </summary>
public sealed class RzipBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Rzip";
  /// <inheritdoc/>
  public string DisplayName => "rzip";
  /// <inheritdoc/>
  public string Description => "rzip 2.x: long-range rolling-hash matches over the whole input, then bzip2";
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
