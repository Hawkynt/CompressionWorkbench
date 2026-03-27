using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Ctw;

/// <summary>
/// Exposes Context Tree Weighting (CTW) as a benchmarkable building block.
/// Uses a byte-level context model with depth 2 (previous 2 bytes).
/// For each byte, it predicts using a context hierarchy (order 2, 1, 0, -1).
/// Header: 4-byte LE original size, 1-byte max depth, then bit-packed hit/miss flags
/// followed by miss literal bytes.
/// </summary>
public sealed class CtwBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_CTW";
  /// <inheritdoc/>
  public string DisplayName => "CTW";
  /// <inheritdoc/>
  public string Description => "Context Tree Weighting, optimal universal compression";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  private const int MaxDepth = 2;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write header: 4-byte LE original size, 1-byte max depth.
    Span<byte> header = stackalloc byte[5];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    header[4] = MaxDepth;
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var encoded = CompressBlock(data);
    ms.Write(encoded, 0, encoded.Length);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    offset += 4;
    // data[offset] is max depth (currently always 2).
    offset += 1;

    if (originalSize == 0)
      return [];

    var src = data[offset..];
    return DecompressBlock(src, originalSize);
  }

  private static byte[] CompressBlock(ReadOnlySpan<byte> src) {
    var model = new ContextModel();

    // First pass: determine hit/miss for each byte and collect miss symbols.
    var hits = new bool[src.Length];
    var missSymbols = new List<byte>();

    for (var i = 0; i < src.Length; i++) {
      var symbol = src[i];
      var predicted = Predict(model, src, i);

      if (predicted == symbol) {
        hits[i] = true;
      } else {
        hits[i] = false;
        missSymbols.Add(symbol);
      }

      model.Update(0, symbol);
      if (i >= 1) model.Update(GetContext1(src, i), symbol);
      if (i >= 2) model.Update(GetContext2(src, i), symbol);
    }

    // Encode: bit-packed hit/miss flags, then miss literal bytes.
    var result = new List<byte>();

    // Pack flags: each bit is 1=hit, 0=miss. MSB first within each byte.
    var flagByteCount = (src.Length + 7) / 8;
    for (var byteIdx = 0; byteIdx < flagByteCount; byteIdx++) {
      byte flagByte = 0;
      for (var bit = 0; bit < 8; bit++) {
        var srcIdx = byteIdx * 8 + bit;
        if (srcIdx < src.Length && hits[srcIdx])
          flagByte |= (byte)(0x80 >> bit);
      }
      result.Add(flagByte);
    }

    // Append miss literals.
    result.AddRange(missSymbols);

    return result.ToArray();
  }

  private static byte[] DecompressBlock(ReadOnlySpan<byte> src, int originalSize) {
    var dst = new List<byte>(originalSize);
    var model = new ContextModel();

    // Read bit-packed flags.
    var flagByteCount = (originalSize + 7) / 8;
    if (src.Length < flagByteCount)
      throw new InvalidDataException("Unexpected end of CTW flag data.");

    var missPos = flagByteCount; // position of next miss literal in src

    for (var i = 0; i < originalSize; i++) {
      var byteIdx = i / 8;
      var bitIdx = i % 8;
      var isHit = (src[byteIdx] & (0x80 >> bitIdx)) != 0;

      byte symbol;
      if (isHit) {
        symbol = Predict(model, dst);
      } else {
        if (missPos >= src.Length)
          throw new InvalidDataException("Unexpected end of CTW miss data.");
        symbol = src[missPos++];
      }

      dst.Add(symbol);

      var idx = dst.Count - 1;
      model.Update(0, symbol);
      if (idx >= 1) model.Update(GetContext1FromList(dst, idx), symbol);
      if (idx >= 2) model.Update(GetContext2FromList(dst, idx), symbol);
    }

    if (dst.Count != originalSize)
      throw new InvalidDataException($"CTW decompressed size mismatch: expected {originalSize}, got {dst.Count}.");

    return dst.ToArray();
  }

  /// <summary>
  /// Predicts the next byte using the context hierarchy (order 2, 1, 0, fallback 0).
  /// </summary>
  private static byte Predict(ContextModel model, ReadOnlySpan<byte> data, int pos) {
    if (pos >= 2) {
      var ctx2 = GetContext2(data, pos);
      var pred = model.GetMostFrequent(ctx2);
      if (pred >= 0) return (byte)pred;
    }
    if (pos >= 1) {
      var ctx1 = GetContext1(data, pos);
      var pred = model.GetMostFrequent(ctx1);
      if (pred >= 0) return (byte)pred;
    }
    {
      var pred = model.GetMostFrequent(0);
      if (pred >= 0) return (byte)pred;
    }
    return 0;
  }

  /// <summary>
  /// Predicts using the decompressor's output list.
  /// </summary>
  private static byte Predict(ContextModel model, List<byte> data) {
    var pos = data.Count;
    if (pos >= 2) {
      var ctx2 = GetContext2FromList(data, pos);
      var pred = model.GetMostFrequent(ctx2);
      if (pred >= 0) return (byte)pred;
    }
    if (pos >= 1) {
      var ctx1 = GetContext1FromList(data, pos);
      var pred = model.GetMostFrequent(ctx1);
      if (pred >= 0) return (byte)pred;
    }
    {
      var pred = model.GetMostFrequent(0);
      if (pred >= 0) return (byte)pred;
    }
    return 0;
  }

  private static int GetContext1(ReadOnlySpan<byte> data, int pos) => 0x100 + data[pos - 1];

  private static int GetContext2(ReadOnlySpan<byte> data, int pos) => 0x10100 + (data[pos - 2] << 8) + data[pos - 1];

  private static int GetContext1FromList(List<byte> data, int pos) => 0x100 + data[pos - 1];

  private static int GetContext2FromList(List<byte> data, int pos) => 0x10100 + (data[pos - 2] << 8) + data[pos - 1];

  /// <summary>
  /// Context model tracking symbol frequencies in each context.
  /// </summary>
  private sealed class ContextModel {
    private readonly Dictionary<int, Dictionary<byte, int>> _contexts = new();

    /// <summary>
    /// Returns the most frequent symbol in the given context, or -1 if no data.
    /// </summary>
    public int GetMostFrequent(int contextId) {
      if (!_contexts.TryGetValue(contextId, out var freqs) || freqs.Count == 0)
        return -1;

      var bestSymbol = -1;
      var bestCount = 0;
      foreach (var (symbol, count) in freqs) {
        if (count > bestCount) {
          bestCount = count;
          bestSymbol = symbol;
        }
      }
      return bestSymbol;
    }

    public void Update(int contextId, byte symbol) {
      if (!_contexts.TryGetValue(contextId, out var freqs)) {
        freqs = new Dictionary<byte, int>();
        _contexts[contextId] = freqs;
      }
      freqs[symbol] = freqs.GetValueOrDefault(symbol) + 1;
    }
  }
}
