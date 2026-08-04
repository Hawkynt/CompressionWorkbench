using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// Exposes PPMd (Prediction by Partial Matching, variant H) as a benchmarkable
/// building block. Wraps the existing <see cref="PpmdModelH"/> context-tree model
/// with <see cref="PpmdRangeEncoder"/>/<see cref="PpmdRangeDecoder"/> range coding.
/// Unlike the simpler order-2 fallback used by the plain <c>BB_PPM</c> block, this
/// uses a full context trie with per-context escape estimation (PPM Method D) and
/// periodic rescaling, matching the model family 7-Zip calls "PPMd".
/// Header: 1-byte order, 4-byte LE original size, then the range-coded stream.
/// </summary>
public sealed class PpmdBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Ppmd";
  /// <inheritdoc/>
  public string DisplayName => "PPMd";
  /// <inheritdoc/>
  public string Description => "PPMd variant H context-tree modeling with range coding (the model family 7-Zip calls PPMd)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  private const int Order = PpmdConstants.DefaultOrder;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Header: 1-byte order, 4-byte LE original size.
    ms.WriteByte((byte)PpmdBuildingBlock.Order);
    Span<byte> sizeHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
    ms.Write(sizeHeader);

    if (data.Length == 0)
      return ms.ToArray();

    var model = new PpmdModelH(PpmdBuildingBlock.Order, PpmdConstants.DefaultMemorySize);
    var encoder = new PpmdRangeEncoder(ms);
    foreach (var b in data)
      model.EncodeSymbol(encoder, b);
    encoder.Finish();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var order = data[0];
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data[1..]);

    if (originalSize == 0)
      return [];

    using var ms = new MemoryStream(data[5..].ToArray());
    var model = new PpmdModelH(order, PpmdConstants.DefaultMemorySize);
    var decoder = new PpmdRangeDecoder(ms);

    var result = new byte[originalSize];
    for (var i = 0; i < originalSize; ++i)
      result[i] = model.DecodeSymbol(decoder);

    return result;
  }
}
