using System.Buffers.Binary;
using Compression.Core.Transforms;

namespace FileFormat.StuffItX;

/// <summary>
/// Writes Cyanide streams (method 1) that <see cref="StuffItXCyanide"/> reads: each block is
/// Burrows-Wheeler transformed, its bytes turned into "move one from front" ranks, and the ranks
/// range coded with the decoder's own adaptive models, kept in step symbol by symbol.
/// </summary>
internal static class StuffItXCyanideEncoder {
  private const int BlockSize = 1 << 20;
  private static readonly int[] MarkovGroups = [0, 1, 2, 3, 4, 5, 6, 7, 8, 3, 9, 10, 3, 4, 5, 11, 11, 8, 6, 2, 5, 6, 7, 8, 12, 12, 13];

  /// <summary>The coded stream of <paramref name="data"/>.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    output.WriteByte(0); // the leading byte the decoder skips
    Span<byte> header = stackalloc byte[9];
    for (var start = 0; start < data.Length; start += BlockSize) {
      var block = data.Slice(start, Math.Min(BlockSize, data.Length - start));
      var (sorted, firstIndex) = BurrowsWheelerTransform.Forward(block);
      var ranks = EncodeM1ff(sorted, 2);
      var maxRank = 0;
      foreach (var r in ranks) maxRank = Math.Max(maxRank, r);
      var symbolCount = Math.Max(0, maxRank - 1);
      header[0] = 0x77;
      BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)block.Length);
      BinaryPrimitives.WriteUInt32BigEndian(header[5..], (uint)firstIndex);
      output.Write(header);
      output.WriteByte((byte)symbolCount);
      output.Write(CodeRanks(ranks, symbolCount));
    }
    output.WriteByte(0xFF);
    return output.ToArray();
  }

  /// <summary>"Move one from front" ranks of <paramref name="block"/>, the inverse of <see cref="StuffItXTransforms.DecodeM1ff"/>.</summary>
  internal static byte[] EncodeM1ff(ReadOnlySpan<byte> block, int order) {
    Span<byte> table = stackalloc byte[256];
    for (var i = 0; i < 256; ++i) table[i] = (byte)i;
    var ranks = new byte[block.Length];
    var lastHead = order - 1;
    for (var i = 0; i < block.Length; ++i) {
      var symbol = table.IndexOf(block[i]);
      ranks[i] = (byte)symbol;
      if (symbol == 0) lastHead = 0;
      else if (symbol == 1) {
        if (lastHead >= order) (table[0], table[1]) = (table[1], table[0]);
      } else {
        var value = table[symbol];
        for (var k = symbol; k > 1; --k) table[k] = table[k - 1];
        table[1] = value;
      }
      ++lastHead;
    }
    return ranks;
  }

  private sealed class FrequencyModel {
    public readonly int Count;
    public readonly uint[] Frequencies = new uint[256];
    public readonly int[] Mapping = new int[256];

    public FrequencyModel(int count) {
      this.Count = count;
      for (var i = 0; i < count; ++i) {
        this.Frequencies[i] = 1;
        this.Mapping[i] = count - 1 - i;
      }
    }

    public ReadOnlySpan<uint> Table => this.Frequencies.AsSpan(0, this.Count);

    public int IndexOf(int value) {
      for (var i = 0; i < this.Count; ++i)
        if (this.Mapping[i] == value) return i;
      throw new InvalidOperationException("Cyanide: a rank class has no symbol for this value.");
    }

    public int Bump(int index, uint maxTotal) {
      var total = 0u;
      for (var i = 0; i < this.Count; ++i) total += this.Frequencies[i];
      if (total >= maxTotal)
        for (var i = 0; i < this.Count; ++i) this.Frequencies[i] = (this.Frequencies[i] + 1) / 2;
      var freq = this.Frequencies[index];
      var last = index;
      while (last < this.Count - 1 && this.Frequencies[last + 1] == freq) ++last;
      if (last != index) (this.Mapping[index], this.Mapping[last]) = (this.Mapping[last], this.Mapping[index]);
      ++this.Frequencies[last];
      return last;
    }
  }

  private static void Code(StuffItXRangeEncoder coder, ReadOnlySpan<uint> frequencies, int index) {
    var total = 0u;
    var cumulative = 0u;
    for (var i = 0; i < frequencies.Length; ++i) {
      if (i < index) cumulative += frequencies[i];
      total += frequencies[i];
    }
    coder.Scale(total);
    coder.Encode(cumulative, cumulative + frequencies[index]);
  }

  private static void TernaryFrequencies(Span<uint> outFreqs, Span<int> meanings, ReadOnlySpan<uint> inFreqs) {
    uint a = inFreqs[0], b = inFreqs[1], c = inFreqs[2];
    if (a < b) {
      if (a < c) {
        if (b < c) { meanings[0] = 0; meanings[1] = 1; meanings[2] = 2; }
        else { meanings[0] = 0; meanings[1] = 2; meanings[2] = 1; }
      } else { meanings[0] = 2; meanings[1] = 0; meanings[2] = 1; }
    } else {
      if (b < c) {
        if (c < a) { meanings[0] = 1; meanings[1] = 2; meanings[2] = 0; }
        else { meanings[0] = 1; meanings[1] = 0; meanings[2] = 2; }
      } else { meanings[0] = 2; meanings[1] = 1; meanings[2] = 0; }
    }
    for (var i = 0; i < 3; ++i) outFreqs[i] = inFreqs[meanings[i]] + 1;
  }

  private static byte[] CodeRanks(byte[] ranks, int symbolCount) {
    var coder = new StuffItXRangeEncoder(0x10000);
    var markov = new uint[14 * 3];
    var lowBitsModels = new FrequencyModel?[9];
    var classStart = new int[9];
    var remaining = symbolCount;
    var shift = 1;
    while (remaining > 0) {
      var n = 1 << shift;
      if (remaining < (3 << shift)) n = remaining;
      lowBitsModels[shift - 1] = new FrequencyModel(n);
      classStart[shift - 1] = 1 << shift;
      remaining -= n;
      ++shift;
    }
    var classes = shift - 1;
    var highBitModel = new FrequencyModel(shift);

    int prev = 0, prev2 = 0, prev3 = 0;
    var someFlag = true;
    Span<uint> freqs = stackalloc uint[3];
    Span<int> meanings = stackalloc int[3];

    foreach (var rank in ranks) {
      var tresym = Math.Min(2, (int)rank);
      var group = MarkovGroups[prev3 * 9 + prev2 * 3 + prev];
      var m = markov.AsSpan(group * 3, 3);
      TernaryFrequencies(freqs, meanings, m);
      Code(coder, freqs, meanings.IndexOf(tresym));

      if (tresym == 0 && !someFlag && group == 0) {
        someFlag = true;
        m[0] >>= 1;
        m[1] >>= 1;
        m[2] >>= 1;
        m[0] += 3;
      } else {
        if (tresym != 0) someFlag = false;
        var total = freqs[0] + freqs[1] + freqs[2];
        var limit = someFlag ? 4096u : 128u;
        if (total > limit) {
          m[0] >>= 1;
          m[1] >>= 1;
          m[2] >>= 1;
        }
        m[tresym] += 2;

        if (tresym == 2) {
          // rank 2 is class 0; a larger rank r is value r - 1 within the class whose range holds it
          var highBit = 0;
          var low = 0;
          if (rank > 2) {
            var value = rank - 1;
            highBit = -1;
            for (var k = 0; k < classes; ++k)
              if (value >= classStart[k] && value < classStart[k] + lowBitsModels[k]!.Count) {
                highBit = k + 1;
                low = value - classStart[k];
                break;
              }
            if (highBit < 0) throw new InvalidOperationException("Cyanide: a rank lies outside every class.");
          }
          var highBitIndex = highBitModel.IndexOf(highBit);
          Code(coder, highBitModel.Table, highBitIndex);
          var newIndex = highBitModel.Bump(highBitIndex, 0x100);
          highBitModel.Bump(newIndex, 0x10000);
          if (highBit != 0) {
            var lowBitsModel = lowBitsModels[highBit - 1]!;
            var lowBitsIndex = lowBitsModel.IndexOf(low);
            Code(coder, lowBitsModel.Table, lowBitsIndex);
            var max = Math.Min(lowBitsModel.Count * 128, 0x4000);
            lowBitsModel.Bump(lowBitsIndex, (uint)max);
          }
        }
      }
      prev3 = prev2;
      prev2 = prev;
      prev = tresym;
    }
    return coder.Finish();
  }
}
