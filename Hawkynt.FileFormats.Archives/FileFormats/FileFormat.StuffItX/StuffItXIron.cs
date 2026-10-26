namespace FileFormat.StuffItX;

/// <summary>
/// Iron, StuffIt X's method 6: blocks of a Burrows-Wheeler or order-4 sort transform whose
/// move-to-front ranks and run lengths are coded bit by bit with adaptive binary weights and three
/// mixed frequency tables.
/// </summary>
/// <remarks>
/// <para>
/// The stream opens with two flags (ST4 instead of BWT, a weighted recency list instead of plain
/// move-to-front) and nine P2 parameters: three frequency-table limits (as powers of two) and six
/// adaptation shifts. Each block then starts on a byte boundary with an end bit, the P2 block size
/// and a stored bit; a coded block carries its P2 primary index, a skipped byte and the range-coded
/// body.
/// </para>
/// <para>Converted from XADMaster's <c>XADStuffItXIronHandle.m</c> (LGPL-2.1-or-later, see
/// THIRD-PARTY-NOTICE.XADMaster.txt).</para>
/// </remarks>
internal sealed class StuffItXIron {
  private const int MaxBlockSize = 0x4000000;

  private readonly StuffItXInput _input;
  private readonly bool _st4;
  private readonly bool _fancyMtf;
  private readonly uint _maxFreq1, _maxFreq2, _maxFreq3;
  private readonly int _byteShift1, _byteShift2, _byteShift3;
  private readonly int _countShift1, _countShift2, _countShift3;

  private StuffItXIron(StuffItXInput input) {
    this._input = input;
    this._st4 = input.ReadBit() == 1;
    this._fancyMtf = input.ReadBit() == 1;
    this._maxFreq1 = Power(input.ReadP2());
    this._maxFreq2 = Power(input.ReadP2());
    this._maxFreq3 = Power(input.ReadP2());
    this._byteShift1 = Shift(input.ReadP2());
    this._byteShift2 = Shift(input.ReadP2());
    this._byteShift3 = Shift(input.ReadP2());
    this._countShift1 = Shift(input.ReadP2());
    this._countShift2 = Shift(input.ReadP2());
    this._countShift3 = Shift(input.ReadP2());
  }

  private static uint Power(ulong exponent) => exponent < 32 ? 1u << (int)exponent : throw new InvalidDataException("Iron frequency limit is out of range.");
  private static int Shift(ulong shift) => shift < 32 ? (int)shift : throw new InvalidDataException("Iron adaptation shift is out of range.");

  /// <summary>Decodes up to <paramref name="length"/> bytes of an Iron stream.</summary>
  public static byte[] Decode(StuffItXInput input, long length) {
    var iron = new StuffItXIron(input);
    var output = new MemoryStream();
    while (output.Length < length) {
      var block = iron.NextBlock();
      if (block == null) break;
      output.Write(block);
    }
    var result = output.ToArray();
    return result.Length > length ? result.AsSpan(0, (int)length).ToArray() : result;
  }

  private byte[]? NextBlock() {
    var input = this._input;
    input.Align();
    if (input.ReadBit() == 1) return null;
    var blockSize = input.ReadP2();
    if (blockSize == 0 || blockSize > MaxBlockSize) throw new InvalidDataException("Iron block size is out of range.");
    var size = (int)blockSize;
    var block = new byte[size];

    if (input.ReadBit() == 0) {
      var firstIndex = input.ReadP2();
      if (firstIndex >= blockSize) throw new InvalidDataException("Iron primary index is out of range.");
      input.Align();
      var sorted = new byte[size];
      this.DecodeBlock(sorted, block);
      var table = new uint[size];
      if (this._st4) {
        if (!StuffItXTransforms.UnsortSt4(block, sorted, (int)firstIndex, table)) throw new InvalidDataException("Iron ST4 block is corrupt.");
      } else StuffItXTransforms.UnsortBwt(block, sorted, (int)firstIndex, table);
    } else {
      input.Align();
      for (var i = 0; i < size; ++i) block[i] = (byte)input.ReadByte();
    }
    return block;
  }

  private static int NextBitWithWeight(StuffItXRangeDecoder coder, ref uint weight, int shift) {
    var bit = coder.NextWeightedBit(weight, 0x1000);
    if (bit == 0) weight += (0x1000 - weight) >> shift;
    else weight -= weight >> shift;
    return bit;
  }

  private static int NextBitWithDoubleWeights(StuffItXRangeDecoder coder, ref uint weight1, int shift1, ref uint weight2, int shift2) {
    var bit = coder.NextWeightedBit((weight1 + weight2) / 2, 0x1000);
    if (bit == 0) {
      weight1 += (0x1000 - weight1) >> shift1;
      weight2 += (0x1000 - weight2) >> shift2;
    } else {
      weight1 -= weight1 >> shift1;
      weight2 -= weight2 >> shift2;
    }
    return bit;
  }

  /// <summary>Decodes the transformed block into <paramref name="sorted"/>; <paramref name="scratch"/> holds the symbol history.</summary>
  private void DecodeBlock(byte[] sorted, byte[] scratch) {
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
    var numBytes = 0;
    if (this._fancyMtf) {
      for (var i = 0; i < 257; ++i) {
        order[i] = (uint)i;
        position[i] = (uint)i;
      }
      score[256] = uint.MaxValue;
    } else {
      for (var i = 0; i < 256; ++i) mtf[i] = (byte)i;
    }

    this._input.Skip(1);
    var coder = new StuffItXRangeDecoder(this._input, false, 0);

    int valueHistory = 0, lengthHistory = 0, lastBits = 0, lastByte = 0;
    Span<uint> frequencies = stackalloc uint[4];
    for (var i = 0; i < sorted.Length;) {
      var f2 = lastByte * 4;
      var f3 = ((lengthHistory & 3) * 256 + valueHistory) * 4;
      for (var j = 0; j < 4; ++j) frequencies[j] = mainFrequencies[j] + lastByteFrequencies[f2 + j] + contextFrequencies[f3 + j];

      var symbol = coder.NextSymbol(frequencies);
      mainFrequencies[symbol] += 2;
      lastByteFrequencies[f2 + symbol] += 2;
      contextFrequencies[f3 + symbol] += 2;

      uint total1 = 0, total2 = 0, total3 = 0;
      for (var j = 0; j < 4; ++j) {
        total1 += mainFrequencies[j];
        total2 += lastByteFrequencies[f2 + j];
        total3 += contextFrequencies[f3 + j];
      }
      if (total1 > this._maxFreq1) for (var j = 0; j < 4; ++j) mainFrequencies[j] = (mainFrequencies[j] + 1) / 2;
      if (total2 > this._maxFreq2) for (var j = 0; j < 4; ++j) lastByteFrequencies[f2 + j] /= 2;
      if (total3 > this._maxFreq3) for (var j = 0; j < 4; ++j) contextFrequencies[f3 + j] /= 2;

      int value;
      if (symbol != 3) value = symbol;
      else {
        var bits = 0;
        while (bits < 6) {
          if (NextBitWithDoubleWeights(coder, ref byteLengthWeights[bits], this._byteShift1, ref byteLengthWeights2[lastBits * 8 + bits], this._byteShift2) == 0) break;
          ++bits;
        }
        value = 1;
        for (var j = 0; j <= bits; ++j) value = (value << 1) | NextBitWithWeight(coder, ref byteBitWeights[bits * 128 + value], this._byteShift3);
        ++value;
        lastBits = bits;
      }

      int b;
      var index = (value + 1) & 0xFF;
      if (this._fancyMtf) {
        b = (int)(order[index] & 0xFF);
        scratch[numBytes] = (byte)b;
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
          int b2 = scratch[numBytes - n];
          if (j == 0) score[b2] -= 0x3801;
          else score[b2] -= (uint)(0x800 >> j);
          if (b2 == b) continue;

          // Sink b2 behind every entry that now scores higher; the walk stops at the last real slot.
          var val = position[b2];
          while (val + 1 < 256 && score[order[val + 1]] > score[b2]) {
            order[val] = order[val + 1];
            position[order[val + 1]] = val;
            ++val;
          }
          order[val] = (uint)b2;
          position[b2] = val;
        }
        ++numBytes;
      } else {
        b = mtf[index];
        Array.Copy(mtf, 0, mtf, 1, index);
        mtf[0] = (byte)b;
      }

      var shortValue = value <= 3 ? value : 3;
      var countBits = 0;
      for (;;) {
        if (NextBitWithDoubleWeights(coder, ref countLengthWeights[(shortValue * 16 + lengthHistory) * 24 + countBits], this._countShift1, ref countLengthWeights2[b * 24 + countBits], this._countShift2) == 0) break;
        if (++countBits >= 24) throw new InvalidDataException("Iron run length is out of range.");
      }
      var count = 1;
      for (var j = 0; j < countBits; ++j) count = (count << 1) | NextBitWithWeight(coder, ref countBitWeights[countBits * 24 + j], this._countShift3);

      for (var j = 0; j < count; ++j) {
        if (i >= sorted.Length) throw new InvalidDataException("Iron run overflows its block.");
        sorted[i++] = (byte)b;
      }

      valueHistory = ((valueHistory << 2) | shortValue) & 0xFF;
      lengthHistory = (lengthHistory << 1) & 0x0E;
      if (countBits > 1) lengthHistory |= 1;
      lastByte = b;
    }
  }

  private static uint[] Filled(int count) {
    var a = new uint[count];
    Array.Fill(a, 0x800u);
    return a;
  }
}
