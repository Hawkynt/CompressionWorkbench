// AZO decoding, converted from EggDotNet (https://github.com/akolman/EggDotNet, MIT, Copyright (c) 2026
// Andrew Kolman). See THIRD-PARTY-NOTICE.EggDotNet.txt.

using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Azo;

/// <summary>
/// ESTsoft's AZO compression (EGG method 3): independent blocks, each an LZ77 parse coded with an
/// adaptive binary arithmetic coder — literal and length models predicted from context, a distance
/// history, a small dictionary of recent matches.
/// </summary>
/// <remarks>
/// Stream layout: a 2-byte header ('1', filter), then blocks introduced by three big-endian UInt32 —
/// uncompressed size, compressed size, and their XOR — until a block with a zero size. A block whose
/// compressed size is not at least 8 bytes smaller than its uncompressed size is stored.
/// </remarks>
public static class AzoDecoder {
  private const byte PrivateVersion = 0x31; // '1'
  private const uint ReduceMinSize = 8;

  /// <summary>Decodes an AZO stream into exactly <paramref name="expectedSize"/> bytes.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data, long expectedSize) {
    if (expectedSize > int.MaxValue)
      throw new NotSupportedException("AZO members larger than 2 GiB are not supported.");
    if (data.Length < 2 || data[0] != PrivateVersion)
      throw new InvalidDataException("Missing or invalid AZO header.");
    var output = new byte[expectedSize];
    var produced = 0;
    var pos = 2;
    while (true) {
      if (pos + 12 > data.Length)
        throw new InvalidDataException("Truncated AZO block header.");
      var blockSize = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
      var compressSize = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
      var check = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 8)..]);
      pos += 12;
      if (blockSize < compressSize || (blockSize ^ compressSize) != check)
        throw new InvalidDataException("AZO block size check failed.");
      if (blockSize == 0 || compressSize == 0)
        break;
      if (compressSize > data.Length - pos)
        throw new InvalidDataException("AZO block overruns the stream.");
      if (blockSize > output.Length - produced)
        throw new InvalidDataException("AZO stream holds more data than the member declares.");
      var block = output.AsSpan(produced, (int)blockSize);
      DecodeBlock(data.Slice(pos, (int)compressSize), block);
      pos += (int)compressSize;
      produced += (int)blockSize;
      if (produced == output.Length && pos + 12 > data.Length)
        break; // some writers omit the terminating empty block
    }
    if (produced != output.Length)
      throw new InvalidDataException($"AZO stream produced {produced} bytes, the member declares {expectedSize}.");
    return output;
  }

  private static void DecodeBlock(ReadOnlySpan<byte> input, Span<byte> output) {
    if (input.Length + ReduceMinSize > output.Length) {
      if (input.Length != output.Length)
        throw new InvalidDataException("Corrupt AZO stored block.");
      input.CopyTo(output);
      return;
    }
    var buffer = new byte[output.Length];
    var entropy = new EntropyCode(input.ToArray());
    if (!new BlockCode(buffer).Code(entropy) || entropy.ConsumedBytes != input.Length)
      throw new InvalidDataException("Corrupt AZO block.");
    buffer.CopyTo(output);
  }

  // ── constants ────────────────────────────────────────────────────────────

  private const uint AlphaSize = 1 << 8;
  private const uint MatchLengthCodeSize = 1 << 7;
  private const uint MatchDistCodeSize = 1 << 7;
  private const uint MatchDistSgap = 16;
  private const uint MatchDistGap = 4;
  private const uint MatchMinLength = 2;
  private const uint MatchMinDist = 1;
  private const uint DistanceHistorySize = 1 << 1;
  private const uint DictionarySize = 1 << 7;
  private const uint DictionaryHistorySize = 2;
  private const int AlphaPredictShift = 5;
  private const int LengthPredictShift = 4;
  private const int BoolStateBits = 8;

  private static readonly uint[] MatchLengthCodeTable = [
    2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
    30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 44, 46, 48, 50, 52, 54, 56, 58, 62, 66, 70, 74, 78,
    82, 86, 90, 98, 106, 114, 122, 130, 138, 146, 154, 170, 186, 202, 218, 234, 250, 266, 282, 314, 346, 378,
    410, 442, 474, 506, 538, 602, 666, 730, 794, 858, 922, 986, 1050, 1178, 1306, 1434, 1562, 1690, 1818, 1946,
    2074, 2330, 2586, 2842, 3098, 3354, 3610, 3866, 4122, 4634, 5146, 5658, 6170, 6682, 7194, 7706, 8218, 9242,
    10266, 11290, 12314, 13338, 14362, 15386, 16410, 18458, 20506, 22554, 24602, 26650, 28698, 30746,
  ];

  private static readonly uint[] MatchDistCodeTable = [
    1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 25, 27, 29, 33, 37, 41, 45,
    53, 61, 69, 77, 93, 109, 125, 141, 173, 205, 237, 269, 333, 397, 461, 525, 653, 781, 909, 1037, 1293, 1549,
    1805, 2061, 2573, 3085, 3597, 4109, 5133, 6157, 7181, 8205, 10253, 12301, 14349, 16397, 20493, 24589, 28685,
    32781, 40973, 49165, 57357, 65549, 81933, 98317, 114701, 131085, 163853, 196621, 229389, 262157, 327693,
    393229, 458765, 524301, 655373, 786445, 917517, 1048589, 1310733, 1572877, 1835021, 2097165, 2621453,
    3145741, 3670029, 4194317, 5242893, 6291469, 7340045, 8388621, 10485773, 12582925, 14680077, 16777229,
    20971533, 25165837, 29360141, 33554445, 41943053, 50331661, 58720269, 67108877, 83886093, 100663309,
    117440525, 134217741, 167772173, 201326605, 234881037, 268435469, 335544333, 402653197, 469762061,
    536870925, 671088653, 805306381, 939524109,
  ];

  private static readonly uint[] MatchLengthExtraBits = [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4,
    4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8,
    8, 8, 9, 9, 9, 9, 9, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11,
  ];

  private static readonly uint[] MatchDistExtraBits = [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4,
    4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11, 12,
    12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15, 16, 16, 16, 16, 17, 17, 17, 17, 18, 18, 18,
    18, 19, 19, 19, 19, 20, 20, 20, 20, 21, 21, 21, 21, 22, 22, 22, 22, 23, 23, 23, 23, 24, 24, 24, 24, 25,
    25, 25, 25, 26, 26, 26, 26, 27, 27, 27, 27,
  ];

  /// <summary>log2 as the reference computes it (double division), so rounding matches its integer casts.</summary>
  private static double Log2(uint x) => Math.Log(x) / Math.Log(2);

  private static uint MatchDistCode(uint value) {
    value -= MatchMinDist;
    if (value < MatchDistSgap)
      return value;
    value -= MatchDistSgap;
    var extraBit = (int)Log2(value / MatchDistGap + 1);
    return (uint)(MatchDistSgap + extraBit * MatchDistGap + (value - ((1u << extraBit) - 1) * MatchDistGap) / (1u << extraBit));
  }

  // ── bit input and arithmetic decoder ────────────────────────────────────

  private sealed class BitCode(byte[] input) {
    private readonly uint _totalBits = (uint)input.Length * 8;
    private uint _index;
    private uint _remain = 8;
    private uint _read;

    public uint ReadBits => this._read;

    public byte Read8() {
      // all callers read whole bytes from a byte boundary (initialisation)
      if (this._totalBits < this._read + 8)
        return 0;
      this._read += 8;
      return input[this._index++];
    }

    public bool ReadBit() {
      if (this._totalBits == this._read)
        return false;
      ++this._read;
      --this._remain;
      if (this._index >= input.Length)
        return false;
      var v = (input[this._index] & (1u << (int)this._remain)) != 0;
      if (this._remain == 0) {
        ++this._index;
        this._remain = 8;
      }
      return v;
    }
  }

  private sealed class EntropyCode {
    private const uint Msb = 0x80000000;
    private const uint SecondMsb = 0x40000000;
    private readonly BitCode _bits;
    private uint _low;
    private uint _up = uint.MaxValue;
    private uint _tag;

    public EntropyCode(byte[] input) {
      this._bits = new BitCode(input);
      for (var i = 0; i < 4; ++i)
        this._tag |= (uint)this._bits.Read8() << ((3 - i) * 8);
    }

    public long ConsumedBytes => (this._bits.ReadBits + 7) / 8;

    private void Rescale() {
      while ((this._low & Msb) == (this._up & Msb)) {
        this._tag = (this._tag << 1) | (this._bits.ReadBit() ? 1u : 0);
        this._low <<= 1;
        this._up = (this._up << 1) | 1;
      }
      while ((this._low & SecondMsb) != 0 && (this._up & SecondMsb) == 0) {
        this._tag = ((this._tag << 1) | (this._bits.ReadBit() ? 1u : 0)) ^ Msb;
        this._low = (this._low << 1) & (Msb - 1);
        this._up = (this._up << 1) | 1 | Msb;
      }
    }

    private uint Step(int totalBit) => this._low == 0 && this._up == uint.MaxValue
      ? 1u << (32 - totalBit)
      : (this._up - this._low + 1) >> totalBit;

    /// <summary>A value of <paramref name="totalBit"/> equiprobable bits.</summary>
    public uint DecodeUniform(uint totalBit) {
      var t = this.Step((int)totalBit);
      var v = (this._tag - this._low) / t;
      this._up = this._low + t * (v + 1) - 1;
      this._low += t * v;
      this.Rescale();
      return v;
    }

    /// <summary>One binary decision whose zero side has probability <paramref name="cumCount"/> / 2^<paramref name="totalBit"/>.</summary>
    public bool Decode(uint cumCount, int totalBit) {
      var t = this.Step(totalBit);
      var v = (this._tag - this._low) / t;
      if (v >= cumCount)
        this._low += t * cumCount;
      else
        this._up = this._low + t * cumCount - 1;
      this.Rescale();
      return v >= cumCount;
    }
  }

  // ── adaptive models ──────────────────────────────────────────────────────

  private sealed class BoolState {
    private const int TotalBit = 12;
    private const uint TotalCount = 1u << TotalBit;
    private const uint ArrayN = 1u << BoolStateBits;
    private readonly uint[] _prob = [.. Enumerable.Repeat(TotalCount / 2, (int)ArrayN)];
    private uint _state;

    public bool Decode(EntropyCode entropy) {
      var b = entropy.Decode(this._prob[this._state], TotalBit);
      const int shift = TotalBit - 6;
      ref var p = ref this._prob[this._state];
      if (!b) p += (TotalCount - p) >> shift;
      else p -= p >> shift;
      this._state = ((this._state << 1) & (ArrayN - 1)) | (b ? 1u : 0);
      return b;
    }
  }

  private sealed class EntropyBitProb {
    private const int TotalBit = 10;
    private const uint TotalCount = 1u << TotalBit;
    private readonly int _bitN;
    private readonly uint[] _prob;

    public EntropyBitProb(uint n) {
      this._bitN = (int)Log2(n - 1) + 1;
      this._prob = [.. Enumerable.Repeat(TotalCount / 2, 1 << this._bitN)];
    }

    public uint Decode(EntropyCode entropy) {
      uint value = 0, pre = 1;
      for (var i = this._bitN - 1; i >= 0; --i) {
        var v = entropy.Decode(this._prob[pre], TotalBit);
        if (v) value |= 1u << i;
        pre = (pre << 1) | (v ? 1u : 0);
      }
      this.Update(value);
      return value;
    }

    public void Update(uint value) {
      uint pre = 1;
      for (var i = this._bitN - 1; i >= 0; --i) {
        var v = (value >> i) & 1;
        const int shift = TotalBit - 6;
        ref var p = ref this._prob[pre];
        if (v == 0) p += (TotalCount - p) >> shift;
        else p -= p >> shift;
        pre = (pre << 1) | v;
      }
    }

    /// <summary>Which of two models gave <paramref name="value"/> the higher probability (sign of the comparison).</summary>
    public int Compare(EntropyBitProb other, uint value) {
      uint prob1 = 1, prob2 = 1, pre = 1;
      for (var i = this._bitN - 1; i >= 0; --i) {
        var p1 = this._prob[pre];
        var p2 = other._prob[pre];
        var v = ((value >> i) & 1) != 0;
        if (((prob1 | prob2) & (((1u << TotalBit) - 1) << (32 - TotalBit))) != 0) {
          prob1 >>= TotalBit;
          prob2 >>= TotalBit;
        }
        prob1 *= v ? TotalCount - p1 : p1;
        prob2 *= v ? TotalCount - p2 : p2;
        pre = (pre << 1) | (v ? 1u : 0);
      }
      return prob1.CompareTo(prob2);
    }
  }

  private sealed class PredictProb {
    private readonly int _shift;
    private readonly EntropyBitProb[] _prob1;
    private readonly EntropyBitProb[] _prob2;
    private readonly int[] _lucky;

    public PredictProb(uint key, uint n, int shift) {
      this._shift = shift;
      this._prob1 = [.. Enumerable.Range(0, (int)key).Select(_ => new EntropyBitProb(n))];
      this._prob2 = [.. Enumerable.Range(0, (int)key >> shift).Select(_ => new EntropyBitProb(n))];
      this._lucky = new int[key];
    }

    public uint Decode(EntropyCode entropy, uint pre) {
      var p1 = this._prob1[pre];
      var p2 = this._prob2[pre >> this._shift];
      uint v;
      if (this._lucky[pre] >= 0) {
        v = p1.Decode(entropy);
        p2.Update(v);
      } else {
        v = p2.Decode(entropy);
        p1.Update(v);
      }
      var r = p1.Compare(p2, v);
      if (r > 0) ++this._lucky[pre];
      else if (r < 0) --this._lucky[pre];
      return v;
    }
  }

  private sealed class HistoryList {
    private readonly uint[] _rep;
    private readonly BoolState _state = new();
    private readonly EntropyBitProb _prob;

    public HistoryList(uint init, uint n) {
      this._rep = [.. Enumerable.Range(0, (int)n).Select(i => init + (uint)i)];
      this._prob = new EntropyBitProb(n);
    }

    public void Add(uint value) => this.Add(value, (uint)this._rep.Length - 1);

    private void Add(uint value, uint delIdx) {
      Array.Copy(this._rep, 0, this._rep, 1, delIdx);
      this._rep[0] = value;
    }

    public bool TryDecode(EntropyCode entropy, out uint value) {
      value = 0;
      if (!this._state.Decode(entropy))
        return false;
      var idx = this._prob.Decode(entropy);
      if (idx >= this._rep.Length)
        throw new InvalidDataException("Corrupt AZO history index.");
      value = this._rep[idx];
      this.Add(value, idx);
      return true;
    }
  }

  private sealed class SymbolCode(uint n, uint historyN) {
    private readonly HistoryList _history = new(0, historyN);
    private readonly EntropyBitProb _prob = new(n);

    public uint Decode(EntropyCode entropy) {
      if (this._history.TryDecode(entropy, out var ret))
        return ret;
      ret = this._prob.Decode(entropy);
      this._history.Add(ret);
      return ret;
    }
  }

  private sealed class DictionaryTable {
    private readonly (uint Position, uint Length)[] _data = [.. Enumerable.Range(0, (int)DictionarySize).Select(i => (0u, MatchMinLength + (uint)i))];
    private readonly BoolState _findState = new();
    private readonly SymbolCode _prob = new(DictionarySize, DictionaryHistorySize);

    public bool TryDecode(EntropyCode entropy, out uint pos, out uint len) {
      pos = len = 0;
      if (!this._findState.Decode(entropy))
        return false;
      var n = this._prob.Decode(entropy);
      if (n >= DictionarySize)
        return false;
      (pos, len) = this._data[n];
      Array.Copy(this._data, 0, this._data, 1, n);
      this._data[0] = (pos, len);
      return true;
    }

    public void Add(uint pos, uint length) {
      Array.Copy(this._data, 0, this._data, 1, this._data.Length - 1);
      this._data[0] = (pos, length);
    }
  }

  private sealed class BlockCode(byte[] buffer) {
    private readonly BoolState _matchState = new();
    private readonly PredictProb _alpha = new(AlphaSize, AlphaSize, AlphaPredictShift);
    private readonly DictionaryTable _dictionary = new();
    private readonly HistoryList _distHistory = new(MatchMinDist, DistanceHistorySize);
    private readonly EntropyBitProb _distProb = new(MatchDistCodeSize);
    private readonly PredictProb _lenProb = new(MatchDistCodeSize, MatchLengthCodeSize, LengthPredictShift);

    private uint DecodeDistance(EntropyCode entropy) {
      if (this._distHistory.TryDecode(entropy, out var dist))
        return dist;
      var idx = this._distProb.Decode(entropy);
      if (idx >= MatchDistCodeTable.Length)
        throw new InvalidDataException("Corrupt AZO distance code.");
      dist = MatchDistCodeTable[idx] + entropy.DecodeUniform(MatchDistExtraBits[idx]);
      this._distHistory.Add(dist);
      return dist;
    }

    private uint DecodeLength(EntropyCode entropy, uint distCode) {
      if (distCode >= MatchDistCodeSize)
        throw new InvalidDataException("Corrupt AZO distance code.");
      var lenCode = this._lenProb.Decode(entropy, distCode);
      if (lenCode >= MatchLengthCodeTable.Length)
        throw new InvalidDataException("Corrupt AZO length code.");
      return MatchLengthCodeTable[lenCode] + entropy.DecodeUniform(MatchLengthExtraBits[lenCode]);
    }

    public bool Code(EntropyCode entropy) {
      buffer[0] = (byte)this._alpha.Decode(entropy, 0);
      for (uint i = 1; i < buffer.Length;) {
        if (!this._matchState.Decode(entropy)) {
          buffer[i] = (byte)this._alpha.Decode(entropy, buffer[i - 1]);
          ++i;
          continue;
        }
        uint dist, length;
        if (this._dictionary.TryDecode(entropy, out var dictPos, out length))
          dist = i - dictPos;
        else {
          dist = this.DecodeDistance(entropy);
          length = this.DecodeLength(entropy, MatchDistCode(dist));
          this._dictionary.Add(i, length);
        }
        if (i < dist || i + length > buffer.Length || length == 0)
          return false;
        for (var k = 0u; k < length; ++k)
          buffer[i + k] = buffer[i - dist + k];
        i += length;
      }
      return true;
    }
  }
}
