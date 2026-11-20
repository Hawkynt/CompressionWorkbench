using Compression.Core.Transforms;

namespace FileFormat.StuffItX;

/// <summary>
/// Writes Iron streams (method 6) that <see cref="StuffItXIron"/> reads: Burrows-Wheeler blocks
/// whose bytes become runs of a recency-list index and a length, both coded bit by bit with the
/// decoder's own adaptive weights and frequency tables, kept in step symbol by symbol.
/// </summary>
/// <remarks>
/// Iron's range coder subtracts from its code and renormalises on the top byte only, which is the
/// classic carry-propagating coder: the encoder keeps a 33-bit low bound and holds back a byte (and
/// any 0xFF bytes after it) until a carry can no longer reach it. The byte the decoder skips before
/// its four code bytes is that held-back first byte.
/// </remarks>
internal static class StuffItXIronEncoder {
  private const int BlockSize = 1 << 20;

  // Frequency limits are 2^value; the shifts set how fast the binary weights adapt.
  private const int MaxFreqExponent1 = 12, MaxFreqExponent2 = 12, MaxFreqExponent3 = 12;
  private const int ByteShift1 = 4, ByteShift2 = 4, ByteShift3 = 5;
  private const int CountShift1 = 4, CountShift2 = 4, CountShift3 = 5;

  /// <summary>The coded stream of <paramref name="data"/>.</summary>
  /// <param name="fancyMtf">Use the weighted recency list rather than plain move-to-front.</param>
  public static byte[] Encode(ReadOnlySpan<byte> data, bool fancyMtf = true) {
    var w = new StuffItXWriter.BitWriter();
    w.Bits(0, 1); // BWT, not ST4
    w.Bits(fancyMtf ? 1u : 0u, 1);
    foreach (var p in (ReadOnlySpan<int>)[MaxFreqExponent1, MaxFreqExponent2, MaxFreqExponent3, ByteShift1, ByteShift2, ByteShift3, CountShift1, CountShift2, CountShift3])
      w.P2((ulong)p);
    for (var start = 0; start < data.Length; start += BlockSize) {
      var block = data.Slice(start, Math.Min(BlockSize, data.Length - start));
      w.Align();
      w.Bits(0, 1); // another block
      w.P2((ulong)block.Length);
      var (sorted, firstIndex) = BurrowsWheelerTransform.Forward(block);
      var coded = CodeBlock(sorted, fancyMtf);
      if (coded == null || coded.Length >= block.Length) {
        w.Bits(1, 1); // stored
        w.Bytes(block);
        continue;
      }
      w.Bits(0, 1); // coded
      w.P2((ulong)firstIndex);
      w.Bytes(coded);
    }
    w.Align();
    w.Bits(1, 1); // end
    return w.ToArray();
  }

  /// <summary>Carry-propagating range encoder matching <see cref="StuffItXRangeDecoder"/> without a low bound.</summary>
  private sealed class RangeEncoder {
    private readonly MemoryStream _out = new();
    private ulong _low;
    private uint _range = 0xFFFFFFFF;
    private byte _cache;
    private long _cacheSize = 1;

    public void Encode(uint scale, uint lowCount, uint highCount) {
      this._range /= scale;
      this._low += (ulong)this._range * lowCount;
      this._range *= highCount - lowCount;
      while (this._range < 0x1000000) {
        this._range <<= 8;
        this.ShiftLow();
      }
    }

    private void ShiftLow() {
      if (this._low < 0xFF000000 || this._low > 0xFFFFFFFF) {
        var carry = (byte)(this._low >> 32);
        var temp = this._cache;
        do {
          this._out.WriteByte((byte)(temp + carry));
          temp = 0xFF;
        } while (--this._cacheSize != 0);
        this._cache = (byte)(this._low >> 24);
      }
      ++this._cacheSize;
      this._low = (this._low & 0x00FFFFFF) << 8;
    }

    public byte[] Finish() {
      for (var i = 0; i < 5; ++i) this.ShiftLow();
      return this._out.ToArray();
    }
  }

  private static void Bit(RangeEncoder coder, int bit, ref uint weight, int shift) {
    if (bit == 0) {
      coder.Encode(0x1000, 0, weight);
      weight += (0x1000 - weight) >> shift;
    } else {
      coder.Encode(0x1000, weight, 0x1000);
      weight -= weight >> shift;
    }
  }

  private static void Bit(RangeEncoder coder, int bit, ref uint weight1, int shift1, ref uint weight2, int shift2) {
    var weight = (weight1 + weight2) / 2;
    if (bit == 0) {
      coder.Encode(0x1000, 0, weight);
      weight1 += (0x1000 - weight1) >> shift1;
      weight2 += (0x1000 - weight2) >> shift2;
    } else {
      coder.Encode(0x1000, weight, 0x1000);
      weight1 -= weight1 >> shift1;
      weight2 -= weight2 >> shift2;
    }
  }

  private static uint[] Filled(int count) {
    var a = new uint[count];
    Array.Fill(a, 0x800u);
    return a;
  }

  /// <summary>
  /// The coded body of a transformed block, or null when the weighted recency list would sink a
  /// byte past its last slot (a state the decoder refuses; such blocks are stored instead).
  /// </summary>
  private static byte[]? CodeBlock(byte[] sorted, bool fancyMtf) {
    uint maxFreq1 = 1u << MaxFreqExponent1, maxFreq2 = 1u << MaxFreqExponent2, maxFreq3 = 1u << MaxFreqExponent3;
    var mainFrequencies = new uint[] { 1, 1, 1, 1 };
    var lastByteFrequencies = new uint[256 * 4];
    var contextFrequencies = new uint[4 * 256 * 4];
    var byteLengthWeights = Filled(8);
    var byteLengthWeights2 = Filled(8 * 8);
    var byteBitWeights = Filled(8 * 128);
    var countLengthWeights = Filled(4 * 16 * 24);
    var countLengthWeights2 = Filled(256 * 24);
    var countBitWeights = Filled(24 * 24);

    var mtf = new byte[256];
    var order = new uint[257];
    var position = new uint[257];
    var score = new uint[257];
    var history = new byte[sorted.Length];
    var numBytes = 0;
    if (fancyMtf) {
      for (var i = 0; i < 257; ++i) {
        order[i] = (uint)i;
        position[i] = (uint)i;
      }
      score[256] = uint.MaxValue;
    } else {
      for (var i = 0; i < 256; ++i) mtf[i] = (byte)i;
    }

    var coder = new RangeEncoder();
    int valueHistory = 0, lengthHistory = 0, lastBits = 0, lastByte = 0;
    Span<uint> frequencies = stackalloc uint[4];
    for (var i = 0; i < sorted.Length;) {
      int b = sorted[i];
      var count = 1;
      while (i + count < sorted.Length && sorted[i + count] == b && count < 0xFFFFFF) ++count;

      // the list index of b; the decoder maps value v to index (v + 1) & 0xFF
      var index = fancyMtf ? (int)position[b] : Array.IndexOf(mtf, (byte)b);
      var value = index == 0 ? 255 : index - 1;
      var symbol = Math.Min(value, 3);

      var f2 = lastByte * 4;
      var f3 = ((lengthHistory & 3) * 256 + valueHistory) * 4;
      var total = 0u;
      var cumulative = 0u;
      for (var j = 0; j < 4; ++j) {
        frequencies[j] = mainFrequencies[j] + lastByteFrequencies[f2 + j] + contextFrequencies[f3 + j];
        if (j < symbol) cumulative += frequencies[j];
        total += frequencies[j];
      }
      coder.Encode(total, cumulative, cumulative + frequencies[symbol]);
      mainFrequencies[symbol] += 2;
      lastByteFrequencies[f2 + symbol] += 2;
      contextFrequencies[f3 + symbol] += 2;

      uint total1 = 0, total2 = 0, total3 = 0;
      for (var j = 0; j < 4; ++j) {
        total1 += mainFrequencies[j];
        total2 += lastByteFrequencies[f2 + j];
        total3 += contextFrequencies[f3 + j];
      }
      if (total1 > maxFreq1) for (var j = 0; j < 4; ++j) mainFrequencies[j] = (mainFrequencies[j] + 1) / 2;
      if (total2 > maxFreq2) for (var j = 0; j < 4; ++j) lastByteFrequencies[f2 + j] /= 2;
      if (total3 > maxFreq3) for (var j = 0; j < 4; ++j) contextFrequencies[f3 + j] /= 2;

      if (symbol == 3) {
        // value - 1 lies in [2^(bits+1), 2^(bits+2)): bits one-bits (and a zero below six), then its low bits+1 bits
        var v = value - 1;
        var bits = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v) - 1;
        for (var k = 0; k < bits; ++k) Bit(coder, 1, ref byteLengthWeights[k], ByteShift1, ref byteLengthWeights2[lastBits * 8 + k], ByteShift2);
        if (bits < 6) Bit(coder, 0, ref byteLengthWeights[bits], ByteShift1, ref byteLengthWeights2[lastBits * 8 + bits], ByteShift2);
        var partial = 1;
        for (var j = bits; j >= 0; --j) {
          var bit = (v >> j) & 1;
          Bit(coder, bit, ref byteBitWeights[bits * 128 + partial], ByteShift3);
          partial = (partial << 1) | bit;
        }
        lastBits = bits;
      }

      if (fancyMtf) {
        history[numBytes] = (byte)b;
        score[b] += 0x4000;
        for (var k = (int)position[b]; k > 0; --k) {
          order[k] = order[k - 1];
          position[order[k - 1]] = (uint)k;
        }
        order[0] = (uint)b;
        position[b] = 0;
        for (var j = 0; j < 12; ++j) {
          var n = 1 << j;
          if (n > numBytes) continue;
          int b2 = history[numBytes - n];
          if (j == 0) score[b2] -= 0x3801;
          else score[b2] -= (uint)(0x800 >> j);
          if (b2 == b) continue;
          var val = position[b2];
          while (score[order[val + 1]] > score[b2]) {
            if (val + 1 == 256) return null;
            order[val] = order[val + 1];
            position[order[val + 1]] = val;
            ++val;
          }
          order[val] = (uint)b2;
          position[b2] = val;
        }
        ++numBytes;
      } else {
        Array.Copy(mtf, 0, mtf, 1, index);
        mtf[0] = (byte)b;
      }

      var shortValue = value <= 3 ? value : 3;
      var countBits = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)count);
      for (var k = 0; k < countBits; ++k)
        Bit(coder, 1, ref countLengthWeights[(shortValue * 16 + lengthHistory) * 24 + k], CountShift1, ref countLengthWeights2[b * 24 + k], CountShift2);
      Bit(coder, 0, ref countLengthWeights[(shortValue * 16 + lengthHistory) * 24 + countBits], CountShift1, ref countLengthWeights2[b * 24 + countBits], CountShift2);
      for (var j = 0; j < countBits; ++j)
        Bit(coder, (count >> (countBits - 1 - j)) & 1, ref countBitWeights[countBits * 24 + j], CountShift3);

      i += count;
      valueHistory = ((valueHistory << 2) | shortValue) & 0xFF;
      lengthHistory = (lengthHistory << 1) & 0x0E;
      if (countBits > 1) lengthHistory |= 1;
      lastByte = b;
    }
    return coder.Finish();
  }
}
