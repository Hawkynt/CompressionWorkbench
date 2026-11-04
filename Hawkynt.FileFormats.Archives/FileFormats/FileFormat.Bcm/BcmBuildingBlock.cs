using Compression.Registry;

namespace FileFormat.Bcm;

/// <summary>
/// BCM (Ilya Muravyov) as a building block: the BCM 1.x coded stream with bcm's default 16 MiB
/// blocks, i.e. a <c>.bcm</c> file without its 4-byte <c>BCM!</c> id.
/// </summary>
/// <remarks>
/// Each block is the suffix-array Burrows-Wheeler Transform of the input coded by bcm's
/// context-mixing model; see <see cref="BcmStream"/>. Byte-identical to BCM 1.65 (and 1.10 to
/// 1.60, which write the same files); see <c>Compression.Tests/Bcm/ReferenceVectors/README.md</c>.
/// </remarks>
public sealed class BcmBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Bcm";
  /// <inheritdoc/>
  public string DisplayName => "BCM (1.x)";
  /// <inheritdoc/>
  public string Description => "BCM 1.x stream: suffix-array Burrows-Wheeler Transform and bcm's context-mixing arithmetic coder, byte-identical to BCM 1.65";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcmStream.CompressPayload(data, BcmStream.DefaultBlockSize);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray(), writable: false);
    using var output = new MemoryStream();
    BcmStream.DecompressPayload(input, output);
    return output.ToArray();
  }
}
