using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Ppm;

/// <summary>
/// Exposes PPM (Prediction by Partial Matching) as a benchmarkable building block.
/// Uses order-2 context modeling with fallback through orders 2, 1, 0, and -1.
/// Header: 1-byte max order, 4-byte LE original size, then encoded stream of
/// (order+1, symbol) byte pairs.
/// </summary>
public sealed class PpmBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_PPM";
  /// <inheritdoc/>
  public string DisplayName => "PPM";
  /// <inheritdoc/>
  public string Description => "Prediction by Partial Matching, order-2 context modeling";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  private const int MaxOrder = 2;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Header: 1-byte max order, 4-byte LE original size.
    ms.WriteByte(MaxOrder);
    Span<byte> sizeHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
    ms.Write(sizeHeader);

    if (data.Length == 0)
      return ms.ToArray();

    // Context models: order 0, 1, 2.
    var order0 = new Dictionary<int, int>();
    var order1 = new Dictionary<int, Dictionary<int, int>>();
    var order2 = new Dictionary<long, Dictionary<int, int>>();

    for (var i = 0; i < data.Length; i++) {
      var symbol = data[i];
      var encodedOrder = -1;

      // Try order 2.
      if (i >= 2) {
        var ctx2Key = ((long)data[i - 2] << 8) | data[i - 1];
        if (order2.TryGetValue(ctx2Key, out var ctx2Table) && ctx2Table.ContainsKey(symbol)) {
          encodedOrder = 2;
        }
      }

      // Try order 1.
      if (encodedOrder < 0 && i >= 1) {
        var ctx1Key = (int)data[i - 1];
        if (order1.TryGetValue(ctx1Key, out var ctx1Table) && ctx1Table.ContainsKey(symbol)) {
          encodedOrder = 1;
        }
      }

      // Try order 0.
      if (encodedOrder < 0 && order0.ContainsKey(symbol)) {
        encodedOrder = 0;
      }

      // Write: order+1 as a byte, then symbol.
      ms.WriteByte((byte)(encodedOrder + 1));
      ms.WriteByte(symbol);

      // Update all applicable context models.
      if (!order0.TryAdd(symbol, 1))
        order0[symbol]++;

      if (i >= 1) {
        var ctx1Key = (int)data[i - 1];
        if (!order1.TryGetValue(ctx1Key, out var ctx1Table)) {
          ctx1Table = new Dictionary<int, int>();
          order1[ctx1Key] = ctx1Table;
        }
        if (!ctx1Table.TryAdd(symbol, 1))
          ctx1Table[symbol]++;
      }

      if (i >= 2) {
        var ctx2Key = ((long)data[i - 2] << 8) | data[i - 1];
        if (!order2.TryGetValue(ctx2Key, out var ctx2Table)) {
          ctx2Table = new Dictionary<int, int>();
          order2[ctx2Key] = ctx2Table;
        }
        if (!ctx2Table.TryAdd(symbol, 1))
          ctx2Table[symbol]++;
      }
    }

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    // Read header.
    var maxOrderByte = data[offset++];

    var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    offset += 4;

    if (uncompressedSize == 0)
      return [];

    var dst = new List<byte>(uncompressedSize);

    // Context models (reconstructed identically to compressor).
    var order0 = new Dictionary<int, int>();
    var order1 = new Dictionary<int, Dictionary<int, int>>();
    var order2 = new Dictionary<long, Dictionary<int, int>>();

    while (dst.Count < uncompressedSize) {
      if (offset + 1 >= data.Length + 1)
        throw new InvalidDataException("PPM: unexpected end of compressed data.");

      var orderPlusOne = data[offset++];
      if (offset >= data.Length)
        throw new InvalidDataException("PPM: unexpected end of compressed data.");
      var symbol = data[offset++];

      var idx = dst.Count;

      // Update context models.
      if (!order0.TryAdd(symbol, 1))
        order0[symbol]++;

      if (idx >= 1) {
        var ctx1Key = (int)dst[idx - 1];
        if (!order1.TryGetValue(ctx1Key, out var ctx1Table)) {
          ctx1Table = new Dictionary<int, int>();
          order1[ctx1Key] = ctx1Table;
        }
        if (!ctx1Table.TryAdd(symbol, 1))
          ctx1Table[symbol]++;
      }

      if (idx >= 2) {
        var ctx2Key = ((long)dst[idx - 2] << 8) | dst[idx - 1];
        if (!order2.TryGetValue(ctx2Key, out var ctx2Table)) {
          ctx2Table = new Dictionary<int, int>();
          order2[ctx2Key] = ctx2Table;
        }
        if (!ctx2Table.TryAdd(symbol, 1))
          ctx2Table[symbol]++;
      }

      dst.Add(symbol);
    }

    return dst.ToArray();
  }
}
