using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy.ExpGolomb;

/// <summary>
/// Exposes Exponential Golomb coding as a benchmarkable building block.
/// Used in H.264/H.265 video codecs for encoding syntax elements.
/// </summary>
public sealed class ExpGolombBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ExpGolomb";
  /// <inheritdoc/>
  public string DisplayName => "Exp-Golomb";
  /// <inheritdoc/>
  public string Description => "Exponential Golomb coding, used in H.264/H.265";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Header: 4-byte LE original size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0) return ms.ToArray();

    var encoder = new ExpGolombEncoder(ms, order: 0);
    foreach (var b in data)
      encoder.Encode(b);
    encoder.Flush();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0) return [];

    var bitstream = data[4..].ToArray();
    var decoder = new ExpGolombDecoder(bitstream, order: 0);
    var result = new byte[originalSize];

    for (var i = 0; i < originalSize; i++)
      result[i] = (byte)decoder.Decode();

    return result;
  }
}
