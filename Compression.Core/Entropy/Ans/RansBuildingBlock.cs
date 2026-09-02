using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy.Ans;

/// <summary>
/// Exposes rANS (range-variant Asymmetric Numeral Systems) as a benchmarkable building block.
/// </summary>
public sealed class RansBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_rANS";
  /// <inheritdoc/>
  public string DisplayName => "rANS";
  /// <inheritdoc/>
  public string Description => "Range ANS entropy coder, used in AV1, LZFSE";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  private const int ScaleBits = 12;
  private const uint Scale = 1u << ScaleBits;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Header: 4-byte LE original size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0) return ms.ToArray();

    // Count frequencies.
    var freq = new uint[256];
    foreach (var b in data)
      freq[b]++;

    // Normalize.
    var normFreq = RansEncoder.NormalizeFrequencies(freq, data.Length);

    // Write frequency table: count of used symbols, then (symbol, freq16) pairs.
    var used = 0;
    for (var i = 0; i < 256; i++)
      if (normFreq[i] > 0) used++;

    Span<byte> buf2 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)used);
    ms.Write(buf2);

    for (var i = 0; i < 256; i++) {
      if (normFreq[i] == 0) continue;
      ms.WriteByte((byte)i);
      BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)normFreq[i]);
      ms.Write(buf2);
    }

    // Encode.
    var encoder = new RansEncoder();
    var encoded = encoder.Encode(data);

    // Write encoded length + data.
    Span<byte> buf4 = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(buf4, encoded.Length);
    ms.Write(buf4);
    ms.Write(encoded);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    offset += 4;

    if (originalSize == 0) return [];

    // Read frequency table.
    var usedCount = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    offset += 2;

    var normFreq = new uint[256];
    for (var i = 0; i < usedCount; i++) {
      var sym = data[offset++];
      normFreq[sym] = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
      offset += 2;
    }

    // Read encoded data.
    var encodedLen = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    offset += 4;

    var encoded = data.Slice(offset, encodedLen);

    var decoder = new RansDecoder();
    return decoder.Decode(encoded, originalSize, normFreq);
  }
}
