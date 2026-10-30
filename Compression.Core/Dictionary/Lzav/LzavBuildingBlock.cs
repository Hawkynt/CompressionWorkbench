using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzav;

/// <summary>
/// Exposes Aleksey Vaneev's LZAV as a benchmarkable building block: a 4-byte LE uncompressed
/// size, then the format-3 stream that <c>lzav_compress_default</c> writes (see
/// <see cref="LzavCodec"/>).
/// </summary>
/// <remarks>
/// LZAV takes the uncompressed length out of band, so the block carries it in front.
/// Reference: LZAV, https://github.com/avaneev/lzav.
/// </remarks>
public sealed class LzavBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzav";
  /// <inheritdoc/>
  public string DisplayName => "LZAV";
  /// <inheritdoc/>
  public string Description => "Vaneev's LZAV data format 3, as lzav_compress_default writes it: tiered-offset LZ77 with offset-carry bits and base-128 lengths";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var stream = LzavCodec.Compress(data);
    var result = new byte[4 + stream.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    stream.CopyTo(result, 4);
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("LZAV: block is shorter than its 4-byte length.");
    var length = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (length < 0)
      throw new InvalidDataException("LZAV: negative length.");
    return LzavCodec.Decompress(data[4..], length);
  }
}
