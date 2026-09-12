using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// Exposes PPMd (Prediction by Partial Matching, variant H) as a benchmarkable
/// building block. Wraps the existing <see cref="PpmdModelH"/> context-tree model
/// with <see cref="PpmdRangeEncoder"/>/<see cref="PpmdRangeDecoder"/> range coding.
/// Unlike the simpler order-3 fallback used by the plain <c>BB_PPM</c> block, this
/// uses a full context trie with per-context escape estimation (PPM Method D) and
/// periodic rescaling, matching the model family 7-Zip calls "PPMd".
/// Header: 1-byte order, 4-byte LE original size, then the range-coded stream.
/// </summary>
public sealed class PpmdBuildingBlock : IBuildingBlock {
  /// <summary>Minimum PPMd-H model order exposed by the standalone optimizer.</summary>
  public const int MinOrder = 2;

  /// <summary>Maximum model order supported by the managed PPMd-H model.</summary>
  public const int MaxOrder = PpmdConstants.MaxOrder;

  /// <summary>Default PPMd-H model order.</summary>
  public const int DefaultOrder = PpmdConstants.DefaultOrder;

  private readonly int _order;

  /// <summary>Creates a PPMd building block with the default model order.</summary>
  public PpmdBuildingBlock() : this(PpmdBuildingBlock.DefaultOrder) { }

  /// <summary>Creates a PPMd building block with the supplied model order.</summary>
  public PpmdBuildingBlock(int order) {
    if (order is < PpmdBuildingBlock.MinOrder or > PpmdBuildingBlock.MaxOrder)
      throw new ArgumentOutOfRangeException(nameof(order),
        $"PPMd order must be between {PpmdBuildingBlock.MinOrder} and {PpmdBuildingBlock.MaxOrder}.");
    this._order = order;
  }

  /// <inheritdoc/>
  public string Id => "BB_Ppmd";
  /// <inheritdoc/>
  public string DisplayName => "PPMd";
  /// <inheritdoc/>
  public string Description => "PPMd variant H context-tree modeling with range coding (the model family 7-Zip calls PPMd)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Header: 1-byte order, 4-byte LE original size.
    ms.WriteByte((byte)this._order);
    Span<byte> sizeHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
    ms.Write(sizeHeader);

    if (data.Length == 0)
      return ms.ToArray();

    var model = new PpmdModelH(this._order, PpmdConstants.DefaultMemorySize);
    var encoder = new PpmdRangeEncoder(ms);
    foreach (var b in data)
      model.EncodeSymbol(encoder, b);
    encoder.Finish();

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 5)
      throw new InvalidDataException("PPMd: truncated header.");

    var order = data[0];
    if (order is < PpmdBuildingBlock.MinOrder or > PpmdBuildingBlock.MaxOrder)
      throw new InvalidDataException(
        $"PPMd: model order must be between {PpmdBuildingBlock.MinOrder} and {PpmdBuildingBlock.MaxOrder}, got {order}.");

    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data[1..]);
    if (originalSize < 0)
      throw new InvalidDataException("PPMd: negative original length.");
    if (originalSize == 0)
      return [];
    if (data.Length < 10)
      throw new InvalidDataException("PPMd: truncated range-coded payload.");

    using var ms = new MemoryStream(data[5..].ToArray(), writable: false);
    var model = new PpmdModelH(order, PpmdConstants.DefaultMemorySize);
    var decoder = new PpmdRangeDecoder(ms);

    var result = new byte[originalSize];
    for (var i = 0; i < originalSize; ++i)
      result[i] = model.DecodeSymbol(decoder);

    return result;
  }
}
