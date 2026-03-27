using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes byte-oriented range coding as a benchmarkable building block.
/// </summary>
public sealed class RangeCodingBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_RangeCoding";
  /// <inheritdoc/>
  public string DisplayName => "Range Coding";
  /// <inheritdoc/>
  public string Description => "Byte-oriented arithmetic coding variant with carryless normalization";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  private const int NumSymbols = 256;
  private const uint Top = 1u << 24;
  private const uint Bot = 1u << 16;
  private const uint FreqTotal = 1u << 14;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write header: 4-byte LE original size.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var rawFreq = new long[NumSymbols];
    for (var i = 0; i < data.Length; i++)
      rawFreq[data[i]]++;

    var freq = ScaleFrequencies(rawFreq, FreqTotal);

    var cumFreq = new uint[NumSymbols + 1];
    for (var i = 0; i < NumSymbols; i++)
      cumFreq[i + 1] = cumFreq[i] + freq[i];

    // Write frequency table: 256 x 4-byte LE (1024 bytes).
    Span<byte> freqBuf = stackalloc byte[4];
    for (var i = 0; i < NumSymbols; i++) {
      BinaryPrimitives.WriteUInt32LittleEndian(freqBuf, freq[i]);
      ms.Write(freqBuf);
    }

    // Carryless range coder encoder.
    var bytes = new List<byte>();
    uint low = 0;
    uint range = 0xFFFFFFFFu;

    for (var i = 0; i < data.Length; i++) {
      var sym = data[i];
      range /= FreqTotal;
      low += range * cumFreq[sym];
      range *= freq[sym];

      // Normalize.
      while (true) {
        if ((low ^ (low + range)) >= Top) {
          if (range >= Bot) break;
          range = ((uint)(-(int)low)) & (Bot - 1);
        }
        bytes.Add((byte)(low >> 24));
        low <<= 8;
        range <<= 8;
      }
    }

    // Flush 4 bytes.
    bytes.Add((byte)(low >> 24)); low <<= 8;
    bytes.Add((byte)(low >> 24)); low <<= 8;
    bytes.Add((byte)(low >> 24)); low <<= 8;
    bytes.Add((byte)(low >> 24));

    ms.Write(bytes.ToArray(), 0, bytes.Count);
    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    // Read 4-byte LE original size.
    var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    offset += 4;

    if (uncompressedSize == 0)
      return [];

    // Read frequency table: 256 x 4-byte LE.
    var freq = new uint[NumSymbols];
    for (var i = 0; i < NumSymbols; i++) {
      freq[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
      offset += 4;
    }

    var cumFreq = new uint[NumSymbols + 1];
    for (var i = 0; i < NumSymbols; i++)
      cumFreq[i + 1] = cumFreq[i] + freq[i];

    var src = data[offset..].ToArray();
    var srcPos = 0;

    // Read initial 4 bytes into code.
    uint code = 0;
    for (var i = 0; i < 4; i++)
      code = (code << 8) | NextByte(src, ref srcPos);

    var result = new byte[uncompressedSize];
    uint low = 0;
    uint range = 0xFFFFFFFFu;

    for (var i = 0; i < uncompressedSize; i++) {
      range /= FreqTotal;
      var target = (code - low) / range;
      if (target >= FreqTotal) target = FreqTotal - 1;

      // Binary search for symbol.
      var lo = 0;
      var hi = NumSymbols - 1;
      while (lo < hi) {
        var mid = (lo + hi) >> 1;
        if (cumFreq[mid + 1] <= target)
          lo = mid + 1;
        else
          hi = mid;
      }

      result[i] = (byte)lo;

      low += range * cumFreq[lo];
      range *= freq[lo];

      // Normalize (must match encoder exactly).
      while (true) {
        if ((low ^ (low + range)) >= Top) {
          if (range >= Bot) break;
          range = ((uint)(-(int)low)) & (Bot - 1);
        }
        code = (code << 8) | NextByte(src, ref srcPos);
        low <<= 8;
        range <<= 8;
      }
    }

    return result;
  }

  private static uint NextByte(byte[] data, ref int pos) =>
    pos < data.Length ? data[pos++] : 0u;

  private static uint[] ScaleFrequencies(long[] rawFreq, uint targetTotal) {
    var freq = new uint[rawFreq.Length];
    var rawTotal = 0L;
    for (var i = 0; i < rawFreq.Length; i++)
      rawTotal += rawFreq[i];

    if (rawTotal == 0) {
      var each = targetTotal / (uint)rawFreq.Length;
      for (var i = 0; i < rawFreq.Length; i++)
        freq[i] = each;
      var rem = targetTotal - each * (uint)rawFreq.Length;
      for (var i = 0; i < (int)rem; i++)
        freq[i]++;
      return freq;
    }

    var total = 0u;
    for (var i = 0; i < rawFreq.Length; i++) {
      freq[i] = Math.Max(1, (uint)(rawFreq[i] * targetTotal / rawTotal));
      total += freq[i];
    }

    while (total > targetTotal) {
      var maxIdx = 0;
      for (var i = 1; i < freq.Length; i++) {
        if (freq[i] > freq[maxIdx])
          maxIdx = i;
      }
      if (freq[maxIdx] <= 1) break;
      freq[maxIdx]--;
      total--;
    }

    while (total < targetTotal) {
      var bestIdx = 0;
      var bestRatio = double.MaxValue;
      for (var i = 0; i < freq.Length; i++) {
        if (rawFreq[i] > 0) {
          var ratio = (double)freq[i] / rawFreq[i];
          if (ratio < bestRatio) {
            bestRatio = ratio;
            bestIdx = i;
          }
        }
      }
      freq[bestIdx]++;
      total++;
    }

    return freq;
  }
}
