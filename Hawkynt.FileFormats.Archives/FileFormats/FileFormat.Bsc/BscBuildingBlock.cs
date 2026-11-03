using Compression.Registry;

namespace FileFormat.Bsc;

/// <summary>
/// libbsc as a building block: one memory block exactly as <c>bsc_compress</c> writes it, with the
/// bsc tool's default parameters (LZP with a 15-bit hash and 72-byte minimum match, the
/// Burrows-Wheeler Transform, the static QLFC coder, following contexts).
/// </summary>
/// <remarks>
/// The block is the 28-byte libbsc header (block size, data size, mode, primary index and three
/// Adler-32 sums) followed by the payload; it is what a <c>.bsc</c> file carries after each
/// 10-byte block record (see <see cref="BscStream"/>). The output is byte-identical to libbsc
/// 3.3.12 built without OpenMP; see <c>Compression.Tests/Bsc/ReferenceVectors/README.md</c>.
/// </remarks>
public sealed class BscBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Bsc";
  /// <inheritdoc/>
  public string DisplayName => "BSC (libbsc)";
  /// <inheritdoc/>
  public string Description => "libbsc memory block: LZP, Burrows-Wheeler Transform and static QLFC coding with the bsc tool's defaults, byte-identical to libbsc";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => BscStream.EncodeMemoryBlock(data, BscSortingContexts.Following, BscEntropyCoder.Static).Block;

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 28)
      throw new InvalidDataException("BSC: a libbsc block is at least 28 bytes");
    return BscStream.DecodeMemoryBlock(data[..28], data[28..]);
  }
}
