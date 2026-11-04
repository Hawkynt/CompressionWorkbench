namespace FileFormat.StuffItX;

/// <summary>
/// Cyanide, StuffIt X's method 1: blocks of a Burrows-Wheeler transform whose "move one from front"
/// ranks are range coded as a ternary symbol (0, 1, or "larger") under an order-3 Markov context,
/// larger ranks as a bit-length class plus low bits from adaptive frequency models.
/// </summary>
/// <remarks>
/// <para>
/// After one leading byte, each block is a marker (0x77; 0xFF ends the stream), the block size and
/// the BWT primary index as big-endian 32-bit words, the number of rank symbols, then the
/// range-coded ranks.
/// </para>
/// <para>Converted from XADMaster's <c>XADStuffItXCyanideHandle.m</c> (LGPL-2.1-or-later, see
/// THIRD-PARTY-NOTICE.XADMaster.txt).</para>
/// </remarks>
internal static class StuffItXCyanide {
  private const int BlockMarker = 0x77;
  private const int EndMarker = 0xFF;

  private static readonly int[] MarkovGroups = [0, 1, 2, 3, 4, 5, 6, 7, 8, 3, 9, 10, 3, 4, 5, 11, 11, 8, 6, 2, 5, 6, 7, 8, 12, 12, 13];

  /// <summary>Decodes up to <paramref name="length"/> bytes of a Cyanide stream.</summary>
  public static byte[] Decode(StuffItXInput input, long length) {
    var output = new MemoryStream();
    input.ReadByte();
    while (output.Length < length) {
      var marker = input.ReadByte();
      if (marker == EndMarker) break;
      if (marker != BlockMarker) throw new InvalidDataException($"Cyanide block marker 0x{marker:X2} is not valid.");

      var blockSize = input.ReadUInt32BigEndian();
      var firstIndex = input.ReadUInt32BigEndian();
      var symbolCount = input.ReadByte();
      if (blockSize == 0 || blockSize > 0x4000000 || firstIndex >= blockSize) throw new InvalidDataException("Cyanide block header is not valid.");

      var sorted = new byte[blockSize];
      ReadTernaryCodedBlock(input, sorted, symbolCount);
      StuffItXTransforms.DecodeM1ff(sorted, 2);
      var block = new byte[blockSize];
      StuffItXTransforms.UnsortBwt(block, sorted, (int)firstIndex, new uint[blockSize]);
      output.Write(block);
    }
    var result = output.ToArray();
    return result.Length > length ? result.AsSpan(0, (int)length).ToArray() : result;
  }

  private sealed class FrequencyModel {
    public int Count;
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

  private static void ReadTernaryCodedBlock(StuffItXInput input, byte[] sorted, int symbolCount) {
    var coder = new StuffItXRangeDecoder(input, true, 0x10000);
    var markov = new uint[14 * 3];

    var lowBitsModels = new FrequencyModel?[9];
    var remaining = symbolCount;
    var shift = 1;
    while (remaining > 0) {
      var n = 1 << shift;
      if (remaining < (3 << shift)) n = remaining;
      lowBitsModels[shift - 1] = new FrequencyModel(n);
      remaining -= n;
      ++shift;
    }
    var highBitModel = new FrequencyModel(shift);

    int prev = 0, prev2 = 0, prev3 = 0;
    var someFlag = true;
    Span<uint> freqs = stackalloc uint[3];
    Span<int> meanings = stackalloc int[3];

    for (var i = 0; i < sorted.Length; ++i) {
      var group = MarkovGroups[prev3 * 9 + prev2 * 3 + prev];
      var m = markov.AsSpan(group * 3, 3);
      TernaryFrequencies(freqs, meanings, m);
      var tresym = meanings[coder.NextSymbol(freqs)];

      if (tresym == 0 && !someFlag && group == 0) {
        someFlag = true;
        m[0] >>= 1;
        m[1] >>= 1;
        m[2] >>= 1;
        m[0] += 3;
        sorted[i] = 0;
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

        if (tresym <= 1) sorted[i] = (byte)tresym;
        else {
          var highBitIndex = coder.NextSymbol(highBitModel.Table);
          var highBit = highBitModel.Mapping[highBitIndex];
          var newIndex = highBitModel.Bump(highBitIndex, 0x100);
          highBitModel.Bump(newIndex, 0x10000);

          if (highBit == 0) sorted[i] = 2;
          else {
            var lowBitsModel = lowBitsModels[highBit - 1] ?? throw new InvalidDataException("Cyanide rank class is not valid.");
            var lowBitsIndex = coder.NextSymbol(lowBitsModel.Table);
            var lowBits = lowBitsModel.Mapping[lowBitsIndex];
            var max = Math.Min(lowBitsModel.Count * 128, 0x4000);
            lowBitsModel.Bump(lowBitsIndex, (uint)max);
            sorted[i] = (byte)((1 << highBit) + lowBits + 1);
          }
        }
      }
      prev3 = prev2;
      prev2 = prev;
      prev = tresym;
    }
  }
}
