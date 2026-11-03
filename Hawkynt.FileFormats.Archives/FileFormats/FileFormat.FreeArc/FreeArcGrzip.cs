using System.Buffers.Binary;

namespace FileFormat.FreeArc;

/// <summary>
/// libGRZip blocks, as FreeArc's <c>grzip</c> stage stores them: a sequence of blocks, each a
/// 28-byte header followed by its packed bytes, until the stream ends.
/// </summary>
/// <remarks>
/// <para>Converted from the decoding half of libGRZip (Ilya Grebnov, LGPL-2.1-or-later):
/// <c>C_GRZip.cpp</c>'s block decoder, <c>BWT.c</c>, <c>ST4.c</c>, <c>MTF_Ari.c</c>,
/// <c>WFC_Ari.c</c>, <c>LZP.c</c>, <c>Rec_Flt.c</c> and <c>WFC_MTF.h</c>, the copy in FreeArc 0.67's
/// <c>Compression/GRZip</c>; see THIRD-PARTY-NOTICE.GRZip.txt.</para>
/// <para>Block header (little-endian 32-bit words): original size, mode, LZP output size (or the
/// record mode, or the LZP mode of a stored block), BWT/ST4 primary index, packed size, two
/// reserved zeros. A mode of −1 is a stored block (LZP-coded when its LZP mode is non-zero), −2 a
/// record-filtered block holding two or four nested blocks. Any other mode is a bit set: 0x2 ST4
/// (else BWT), 0x4 MTF (else weighted frequency count), LZP hash bits in bits 8..15 and the LZP
/// minimum match length from bit 16 up.</para>
/// </remarks>
internal static class FreeArcGrzip {
  private const int HeaderSize = 28;
  private const int MaxBlockSize = 8 * 1024 * 1024 - 512;
  private const int Log2MaxBlockSize = 23;
  private const int MaxLzpHashBits = 24;
  private const int StoredMode = -1, RecordMode = -2;
  private const int St4Flag = 0x2, MtfFlag = 0x4;
  private const int StrongBwtFlag = 0x40000000;

  /// <summary>Decodes a whole <c>grzip</c> stream; when <paramref name="expectedSize"/> is known (≥ 0) the output must reach it exactly.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> input, long expectedSize) {
    using var output = new MemoryStream();
    var pos = 0;
    while (pos < input.Length) {
      if (input.Length - pos < HeaderSize)
        throw new InvalidDataException("FreeArc grzip: truncated block header.");
      var packed = BinaryPrimitives.ReadInt32LittleEndian(input[(pos + 16)..]);
      if (packed < 0 || packed > input.Length - pos - HeaderSize)
        throw new InvalidDataException("FreeArc grzip: block runs past the end of the stream.");
      output.Write(DecodeBlock(input.Slice(pos, HeaderSize + packed)));
      pos += HeaderSize + packed;
      if (expectedSize >= 0 && output.Length > expectedSize)
        throw new InvalidDataException("FreeArc grzip: stream decodes to more bytes than expected.");
    }
    return output.ToArray();
  }

  private static byte[] DecodeBlock(ReadOnlySpan<byte> block) {
    if (block.Length < HeaderSize)
      throw new InvalidDataException("FreeArc grzip: truncated block header.");
    var size = BinaryPrimitives.ReadInt32LittleEndian(block);
    var mode = BinaryPrimitives.ReadInt32LittleEndian(block[4..]);
    var aux = BinaryPrimitives.ReadInt32LittleEndian(block[8..]);
    var index = BinaryPrimitives.ReadInt32LittleEndian(block[12..]);
    var packed = BinaryPrimitives.ReadInt32LittleEndian(block[16..]);
    if (BinaryPrimitives.ReadInt32LittleEndian(block[20..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(block[24..]) != 0)
      throw new InvalidDataException("FreeArc grzip: reserved block header fields are not zero.");
    if (size is < 0 or > MaxBlockSize || packed < 0 || packed > block.Length - HeaderSize)
      throw new InvalidDataException("FreeArc grzip: block header is out of range.");
    var data = block.Slice(HeaderSize, packed);

    byte[] result;
    try {
      result = mode switch {
        StoredMode when aux == 0 => data.ToArray(),
        StoredMode => LzpDecode(data, size, aux),
        RecordMode => DecodeRecordBlock(data, size, aux),
        < 0 => throw new InvalidDataException($"FreeArc grzip: unknown block mode {mode}."),
        _ => DecodeSortedBlock(data, size, mode, aux, index),
      };
    } catch (IndexOutOfRangeException) {
      throw new InvalidDataException("FreeArc grzip: corrupt block.");
    }
    if (result.Length != size)
      throw new InvalidDataException($"FreeArc grzip: block decoded to {result.Length} bytes, header says {size}.");
    return result;
  }

  /// <summary>Two (record modes 1 and 3) or four (modes 2 and 4) nested blocks, then the record filter undone.</summary>
  private static byte[] DecodeRecordBlock(ReadOnlySpan<byte> data, int size, int recordMode) {
    if (recordMode is < 1 or > 4)
      throw new InvalidDataException($"FreeArc grzip: unknown record mode {recordMode}.");
    var parts = (recordMode & 1) == 1 ? 2 : 4;
    var buffer = new byte[size];
    int pos = 0, filled = 0;
    for (var p = 0; p < parts; ++p) {
      if (data.Length - pos < HeaderSize)
        throw new InvalidDataException("FreeArc grzip: truncated nested block.");
      var packed = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 16)..]);
      if (packed < 0 || packed > data.Length - pos - HeaderSize)
        throw new InvalidDataException("FreeArc grzip: nested block runs past its parent.");
      var part = DecodeBlock(data.Slice(pos, HeaderSize + packed));
      if (part.Length > size - filled)
        throw new InvalidDataException("FreeArc grzip: nested blocks exceed their parent's size.");
      part.CopyTo(buffer, filled);
      filled += part.Length;
      pos += HeaderSize + packed;
    }
    if (filled != size)
      throw new InvalidDataException("FreeArc grzip: nested blocks do not fill their parent.");
    return RecordDecode(buffer, recordMode);
  }

  /// <summary>Entropy decoding (MTF or WFC ranks), the inverse BWT or ST4, then LZP when enabled.</summary>
  private static byte[] DecodeSortedBlock(ReadOnlySpan<byte> data, int size, int mode, int lzpSize, int index) {
    if (lzpSize is < 0 or > MaxBlockSize)
      throw new InvalidDataException("FreeArc grzip: LZP size is out of range.");
    var sorted = RankDecode(data, (lzpSize + 7) & ~7, (mode & MtfFlag) != 0);
    if (sorted.Length < lzpSize)
      throw new InvalidDataException("FreeArc grzip: entropy-coded data is shorter than its block.");
    var text = (mode & St4Flag) != 0 ? St4Decode(sorted, index) : BwtDecode(sorted, index);
    if (mode >> 8 == 0)
      return text.AsSpan(0, lzpSize).ToArray();
    return LzpDecode(text.AsSpan(0, lzpSize), size, mode);
  }

  #region LZP

  private const byte LzpMatchFlag = 0xF2, LzpRunFlag = 0xF3, LzpXorFlag = 0xFF ^ LzpRunFlag;

  private static byte[] LzpDecode(ReadOnlySpan<byte> input, int outputSize, int mode) {
    var minMatch = (mode >> 16) % 32767;
    var hashBits = (mode >> 8) & 0xFF;
    if (hashBits > MaxLzpHashBits)
      throw new NotSupportedException($"FreeArc grzip: an LZP hash of 2^{hashBits} entries is not supported.");
    if (input.Length < 4 || outputSize < 4)
      throw new InvalidDataException("FreeArc grzip: LZP block is shorter than its 4-byte prefix.");
    var mask = (1u << hashBits) - 1;
    var contexts = new int[mask + 1]; // output offsets; 0 = empty (the first stored offset is 4)
    var output = new byte[outputSize];
    input[..4].CopyTo(output);
    var ctx = BinaryPrimitives.ReadUInt32BigEndian(input);
    int ip = 4, op = 4;
    while (ip < input.Length) {
      var hash = ((ctx >> 15) ^ ctx ^ (ctx >> 3)) & mask;
      var pointer = contexts[hash];
      contexts[hash] = op;
      if (pointer == 0) {
        ctx = (ctx << 8) | Put(output, ref op, input[ip++]);
        continue;
      }
      var b = input[ip++];
      if (b != LzpMatchFlag) {
        ctx = (ctx << 8) | Put(output, ref op, b);
        continue;
      }
      uint length = 0;
      byte flag;
      do {
        if (ip >= input.Length)
          throw new InvalidDataException("FreeArc grzip: LZP match length is truncated.");
        flag = input[ip++];
        length += (uint)(flag ^ LzpXorFlag);
      } while (flag == LzpRunFlag);
      if (length == 0) {
        ctx = (ctx << 8) | Put(output, ref op, LzpMatchFlag);
        continue;
      }
      length += (uint)minMatch - 1;
      if (length > (uint)(outputSize - op))
        throw new InvalidDataException("FreeArc grzip: LZP match runs past the block.");
      for (; length > 0; --length)
        output[op++] = output[pointer++];
      ctx = BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(op - 4));
    }
    return op == outputSize ? output : output.AsSpan(0, op).ToArray();
  }

  private static uint Put(byte[] output, ref int op, byte value) {
    if (op >= output.Length)
      throw new InvalidDataException("FreeArc grzip: LZP output runs past the block.");
    output[op++] = value;
    return value;
  }

  #endregion

  #region Record filter

  private static byte[] RecordDecode(byte[] input, int recordMode) {
    var size = input.Length;
    var output = new byte[size];
    switch (recordMode) {
      case 1:
      case 2: {
        var stride = recordMode == 1 ? 2 : 4;
        var ip = 0;
        for (var lane = 0; lane < stride; ++lane)
          for (var i = lane; i < size; i += stride)
            output[i] = input[ip++];
        break;
      }
      case 3: {
        var records = size >> 1;
        ushort previous = 0;
        for (var r = 0; r < records; ++r) {
          var delta = (ushort)((input[r] << 8) | input[r + records]);
          delta = (delta & 1) != 0 ? (ushort)~(delta >> 1) : (ushort)(delta >> 1);
          previous = (ushort)(previous + delta);
          BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2 * r), previous);
        }
        for (var i = 2 * records; i < size; ++i)
          output[i] = input[i];
        break;
      }
      default: {
        var records = size >> 2;
        uint previous = 0;
        for (var r = 0; r < records; ++r) {
          var delta = ((uint)input[r] << 24) | ((uint)input[r + 3 * records] << 16) | ((uint)input[r + 2 * records] << 8) | input[r + records];
          delta = (delta & 1) != 0 ? ~(delta >> 1) : delta >> 1;
          previous += delta;
          BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4 * r), previous);
        }
        for (var i = 4 * records; i < size; ++i)
          output[i] = input[i];
        break;
      }
    }
    return output;
  }

  #endregion

  #region Inverse block sorts

  private static byte[] BwtDecode(byte[] input, int primary) {
    var size = input.Length;
    var strong = (primary & StrongBwtFlag) != 0;
    primary &= ~StrongBwtFlag;
    if (primary < 0 || primary > size || (!strong && primary == size && size > 0))
      throw new InvalidDataException("FreeArc grzip: BWT primary index is out of range.");

    // "Strong" sorting leaves the end-of-block row out of the stream, "fast" sorting keeps it.
    var count = new uint[256];
    var links = new uint[strong ? size + 1 : size];
    for (var i = 0; i < size; ++i) {
      var c = input[i];
      links[strong && i >= primary ? i + 1 : i] = (count[c]++ << 8) | c;
    }
    for (uint sum = strong ? 1u : 0u, c = 0; c < 256; ++c) {
      sum += count[c];
      count[c] = sum - count[c];
    }
    var output = new byte[size];
    var row = strong ? 0u : (uint)primary;
    for (var i = size - 1; i >= 0; --i) {
      var u = links[row];
      var c = (byte)u;
      row = (u >> 8) + count[c];
      output[i] = c;
    }
    return output;
  }

  private const uint St4Indirect = 0x800000;

  /// <summary>Inverse of the order-4 sort transform (Schindler transform).</summary>
  private static byte[] St4Decode(byte[] input, int primary) {
    var size = input.Length;
    if (primary < 0 || primary > size)
      throw new InvalidDataException("FreeArc grzip: ST4 primary index is out of range.");
    var context2 = new int[65536];
    var flag = new byte[(size + 8) >> 3];
    var table = new uint[size + 1];
    var t = new int[256];
    var lastSeen = new int[256];
    Array.Fill(lastSeen, -1);

    foreach (var b in input)
      t[b]++;
    for (int i = 0, j = 0, sum = 0; i < 256; ++i)
      for (sum += t[i], t[i] = sum - t[i]; j < sum; ++j)
        context2[(input[j] << 8) | i]++;

    var s = (int[])t.Clone();
    for (int i = 0, j = 0, sum = 0; i < 65536; ++i) {
      var start = sum;
      for (sum += context2[i]; j < sum; ++j) {
        var c = input[j];
        if (lastSeen[c] != start) {
          lastSeen[c] = start;
          flag[t[c] >> 3] |= (byte)(1 << (t[c] & 7));
        }
        t[c]++;
      }
    }

    Array.Clear(lastSeen);
    for (int i = 0, start = 0; i < size; ++i) {
      var c = input[i];
      if ((flag[i >> 3] & (1 << (i & 7))) != 0)
        start = i;
      if (lastSeen[c] <= start) {
        table[i] = (uint)s[c];
        lastSeen[c] = i + 1;
      } else
        table[i] = (uint)(lastSeen[c] - 1) | St4Indirect;
      s[c]++;
      table[i] |= (uint)c << 24;
    }
    table[size] = St4Indirect;

    var output = new byte[size];
    var k = (uint)primary;
    var entry = table[k];
    for (var i = 0; i < size; ++i) {
      if ((entry & St4Indirect) != 0) {
        k = table[entry & (St4Indirect - 1)]++ & (St4Indirect - 1);
      } else {
        table[k]++;
        k = entry & (St4Indirect - 1);
      }
      entry = table[k];
      output[i] = (byte)(entry >> 24);
    }
    return output;
  }

  #endregion

  #region Rank coding (MTF or WFC) behind the adaptive binary/multi-symbol arithmetic coder

  private const int ModelBits = 11, ModelMaxFreq = 1 << ModelBits;
  private const int Log2RleShift0 = 6, Log2RleShift1 = 3, Log2RleShift2 = 6;
  private const int L1Shift0 = 4, L1Shift1 = 6, L2Shift = 7;
  private const uint L00MaxFreq = 58, L01MaxFreq = 62, L02MaxFreq = 204;
  private const int Log2Slots = Log2MaxBlockSize + 1;
  private const uint Half = ModelMaxFreq >> 1;

  private static readonly uint[] GroupBegin = [3, 5, 9, 17, 33, 65, 129];

  private static readonly uint[] Log2RunBase = [
    1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384,
    32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304, 0,
  ];

  // Weighted-frequency-count weights for the symbols 1, 2, 4, ..., 2048 positions back; they sum to the weight a new symbol gets.
  private const int WfcNewWeight = 131072;
  private static readonly int[] WfcWeights = [114688, 7272, 4240, 2364, 1263, 649, 320, 153, 70, 31, 14, 8];

  private ref struct RangeDecoder(ReadOnlySpan<byte> input) {
    private const uint RangeTop = 1u << 24;
    private readonly ReadOnlySpan<byte> _input = input;
    private int _pos;
    private uint _code;
    private uint _range = uint.MaxValue;

    public void Start() {
      for (var i = 0; i < 5; ++i)
        this._code = (this._code << 8) | this.Next();
    }

    private uint Next() => this._pos < this._input.Length ? this._input[this._pos++] : 0u;

    /// <summary>Scales the range down to <paramref name="total"/> and returns the code's position in it.</summary>
    public uint GetFreq(uint total) => this._code / (this._range /= total);

    public void Decode(uint freq, uint cumFreq) {
      if (freq == 0)
        throw new InvalidDataException("FreeArc grzip: zero-frequency symbol in the arithmetic code.");
      this._code -= cumFreq * this._range;
      this._range *= freq;
      while (this._range < RangeTop) {
        this._code = (this._code << 8) | this.Next();
        this._range <<= 8;
      }
    }

    /// <summary>Decodes one binary decision whose zero has probability <paramref name="p0"/>/2048.</summary>
    public bool Bit(uint p0) {
      if (this.GetFreq(ModelMaxFreq) < p0) {
        this.Decode(p0, 0);
        return false;
      }
      this.Decode(ModelMaxFreq - p0, p0);
      return true;
    }
  }

  private static void Raise(ref uint p, int shift) => p += (ModelMaxFreq - p) >> shift;
  private static void Lower(ref uint p, int shift) => p -= p >> shift;

  private static void Halve(Span<uint> model, bool roundUp) {
    uint sum = 0;
    for (var i = 0; i < 4; ++i)
      sum += model[i] = roundUp ? (model[i] + 1) >> 1 : model[i] >> 1;
    model[4] = sum;
  }

  /// <summary>Decodes runs of ranks until the end-of-block code and maps them back to bytes through an MTF list or a WFC list.</summary>
  private static byte[] RankDecode(ReadOnlySpan<byte> input, int capacity, bool mtf) {
    var coder = new RangeDecoder(input);
    coder.Start();

    var l00 = new uint[] { 1, 1, 1, 1, 4 };
    var l01 = new uint[256 * 5];
    var l02 = new uint[4 * 256 * 5];
    var l10 = new uint[8];
    var l11 = new uint[8 * 8];
    var l20 = new uint[8 * 128];
    var rle0 = new uint[64 * Log2Slots];
    var rle1 = new uint[256 * Log2Slots];
    var rle2 = new uint[Log2Slots * Log2Slots];
    Array.Fill(l10, Half);
    l11.AsSpan(0, 7 * 8).Fill(Half);
    Array.Fill(l20, Half);
    Array.Fill(rle0, Half);
    Array.Fill(rle1, Half);
    Array.Fill(rle2, Half);

    var list = new int[257];
    var position = new int[257];
    var weight = new int[257];
    for (var i = 0; i <= 256; ++i)
      list[i] = position[i] = i;
    weight[256] = -1;
    var history = mtf ? Array.Empty<byte>() : new byte[capacity];
    var historyLength = 0;

    var output = new byte[capacity];
    var outPos = 0;
    int ctxRle = 0, ctxL0 = 0, ctxL1 = 0;
    uint ch = 0;

    while (true) {
      var v = l01.AsSpan((int)ch * 5, 5);
      var u = l02.AsSpan((4 * ctxL0 + (ctxRle & 3)) * 5, 5);
      var frq = coder.GetFreq(l00[4] + u[4] + v[4]);
      uint cum = 0;
      var rank = 0;
      while (true) {
        if (rank > 3)
          throw new InvalidDataException("FreeArc grzip: rank code is out of range.");
        var f = l00[rank] + u[rank] + v[rank];
        if (frq < cum + f) {
          coder.Decode(f, cum);
          break;
        }
        cum += f;
        ++rank;
      }
      l00[rank] += 2; u[rank] += 2; v[rank] += 2;
      l00[4] += 2; u[4] += 2; v[4] += 2;
      if (l00[4] > L00MaxFreq) Halve(l00, true);
      if (v[4] > L01MaxFreq) Halve(v, false);
      if (u[4] > L02MaxFreq) Halve(u, false);

      if (rank == 3) {
        // Group number in unary (at most 6), then the position within the group in binary.
        var groupBase = ctxL1 * 8;
        var group = 0;
        for (; group != 6; ++group) {
          ref var pv = ref l10[group];
          ref var pu = ref l11[groupBase + group];
          if (!coder.Bit((pv + pu) >> 1)) {
            Raise(ref pv, L1Shift0);
            Raise(ref pu, L1Shift1);
            break;
          }
          Lower(ref pv, L1Shift0);
          Lower(ref pu, L1Shift1);
        }
        ctxL1 = group;
        var tree = l20.AsSpan(group * 128, 128);
        int node = 1, groupPos = 0;
        for (var i = 0; i <= group; ++i) {
          ref var p = ref tree[node];
          if (!coder.Bit(p)) {
            Raise(ref p, L2Shift);
            node <<= 1;
            groupPos <<= 1;
          } else {
            Lower(ref p, L2Shift);
            node = (node << 1) | 1;
            groupPos = (groupPos << 1) | 1;
          }
        }
        if (group == 6 && groupPos == 127)
          break; // end of block
        rank = (int)GroupBegin[group] + groupPos;
      }

      var slot = (rank + 1) & 0xFF;
      ch = (uint)list[slot];
      if (mtf) {
        for (var k = slot; k > 0; --k)
          list[k] = list[k - 1];
        list[0] = (int)ch;
      } else {
        if (historyLength >= history.Length)
          throw new InvalidDataException("FreeArc grzip: more runs than the block holds.");
        history[historyLength] = (byte)ch;
        WfcPromote(list, position, weight, (int)ch);
        for (int w = 0, back = 1; w < WfcWeights.Length; ++w, back <<= 1)
          if (historyLength >= back)
            WfcDemote(list, position, weight, history[historyLength - back], WfcWeights[w], (int)ch);
        ++historyLength;
      }

      var rankContext = Math.Min(rank, 3);
      ctxL0 = ((ctxL0 << 2) | rankContext) & 0xFF;

      // Run length: its bit count in unary, then the bits below the leading one.
      var rv = (ctxRle + 16 * rankContext) * Log2Slots;
      var ru = (int)ch * Log2Slots;
      var log2Run = 0;
      while (true) {
        if (log2Run >= Log2Slots)
          throw new InvalidDataException("FreeArc grzip: run length is out of range.");
        ref var pv = ref rle0[rv + log2Run];
        ref var pu = ref rle1[ru + log2Run];
        if (!coder.Bit((pv + pu) >> 1)) {
          Raise(ref pv, Log2RleShift0);
          Raise(ref pu, Log2RleShift1);
          break;
        }
        Lower(ref pv, Log2RleShift0);
        Lower(ref pu, Log2RleShift1);
        ++log2Run;
      }
      ctxRle = ((ctxRle << 1) | (log2Run < 2 ? 0 : 1)) & 0xF;

      var bits = rle2.AsSpan(log2Run * Log2Slots, Log2Slots);
      uint run = 0;
      for (var i = 0; i < log2Run; ++i) {
        ref var p = ref bits[i];
        if (!coder.Bit(p)) {
          Raise(ref p, Log2RleShift2);
          run <<= 1;
        } else {
          Lower(ref p, Log2RleShift2);
          run = (run << 1) | 1;
        }
      }
      run += Log2RunBase[log2Run];
      if (run > (uint)(output.Length - outPos))
        throw new InvalidDataException("FreeArc grzip: run exceeds the block.");
      output.AsSpan(outPos, (int)run).Fill((byte)ch);
      outPos += (int)run;
    }
    return outPos == output.Length ? output : output.AsSpan(0, outPos).ToArray();
  }

  /// <summary>The new symbol gains the full weight and moves to the front.</summary>
  private static void WfcPromote(int[] list, int[] position, int[] weight, int c) {
    weight[c] += WfcNewWeight;
    for (var j = position[c]; j > 0; --j) {
      list[j] = list[j - 1];
      position[list[j]] = j;
    }
    list[0] = c;
    position[c] = 0;
  }

  /// <summary>A symbol seen earlier loses part of its weight and sinks behind every heavier symbol; the current symbol only loses weight.</summary>
  private static void WfcDemote(int[] list, int[] position, int[] weight, int c, int amount, int current) {
    var w = weight[c] -= amount;
    if (c == current)
      return;
    var j = position[c];
    while (weight[list[j + 1]] > w) {
      list[j] = list[j + 1];
      position[list[j]] = j;
      ++j;
    }
    list[j] = c;
    position[c] = j;
  }

  #endregion
}
