#pragma warning disable CS1591

namespace Codec.MonkeysAudio;

/// <summary>
/// Shared constants for Monkey's Audio's range coder, taken verbatim from the
/// reference SDK (<c>MACLib/BitArray.cpp</c>) / ffmpeg <c>libavcodec/apedec.c</c>.
/// <see cref="Counts"/> is the 64-entry cumulative-frequency table the "current"
/// (3.98+) entropy stage uses to map a scaled range cell to an overflow class,
/// <see cref="Widths"/> the matching per-class frequencies, and the model total is
/// <see cref="Total"/> (1&lt;&lt;16). <see cref="KSumBoundary"/> is the reference's
/// <c>K_SUM_MIN_BOUNDARY</c> ladder driving the Rice <c>k</c> adaptation.
/// </summary>
internal static class ApeRangeConstants {

  public const uint Total = 1u << 16; // 65536
  public const int ModelElements = 64;
  public const int OverflowShift = 16;

  // BitArray.cpp RANGE_TOTAL[64] / apedec.c counts_3980 extended to 64 entries, with
  // a 65th sentinel (= Total) so the class-search loop terminates cleanly at the
  // escape class (overflow == 63) where the cumulative value reaches 65535.
  public static readonly uint[] Counts = [
        0, 19578, 36160, 48417, 56323, 60899, 63265, 64435,
    64971, 65232, 65351, 65416, 65447, 65466, 65476, 65482,
    65485, 65488, 65490, 65491, 65492, 65493, 65494, 65495,
    65496, 65497, 65498, 65499, 65500, 65501, 65502, 65503,
    65504, 65505, 65506, 65507, 65508, 65509, 65510, 65511,
    65512, 65513, 65514, 65515, 65516, 65517, 65518, 65519,
    65520, 65521, 65522, 65523, 65524, 65525, 65526, 65527,
    65528, 65529, 65530, 65531, 65532, 65533, 65534, 65535,
    65536,
  ];

  // BitArray.cpp RANGE_WIDTH[64].
  public static readonly uint[] Widths = [
    19578, 16582, 12257, 7906, 4576, 2366, 1170, 536,
    261, 119, 65, 31, 19, 10, 6, 3,
    3, 2, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1,
  ];

  // BitArray.cpp K_SUM_MIN_BOUNDARY[32].
  public static readonly uint[] KSumBoundary = [
    0u, 32u, 64u, 128u, 256u, 512u, 1024u, 2048u, 4096u, 8192u, 16384u, 32768u,
    65536u, 131072u, 262144u, 524288u, 1048576u, 2097152u, 4194304u, 8388608u,
    16777216u, 33554432u, 67108864u, 134217728u, 268435456u, 536870912u,
    1073741824u, 2147483648u, 0u, 0u, 0u, 0u,
  ];
}

/// <summary>
/// 32-bit range decoder for Monkey's Audio v3.9x frames — a byte-exact port of the
/// reference SDK's <c>CUnBitArray</c> range coder (the same coder ffmpeg implements
/// in <c>apedec.c</c> as <c>range_*</c>). The frame payload is the reference's
/// big-endian-within-32-bit-words bit array; this reader walks it word-by-word with
/// the SDK's <c>(buffer&gt;&gt;1)&amp;0xFF</c> carry handling so a stream produced by
/// the reference encoder (or this codec's <see cref="ApeRangeEncoder"/>) decodes
/// bit-for-bit.
/// </summary>
internal sealed class ApeRangeDecoder {
  private const int CodeBits = 32;
  private const uint TopValue = 1u << (CodeBits - 1); // 1<<31
  private const int ShiftBits = CodeBits - 9;         // 23
  private const int ExtraBits = (CodeBits - 2) % 8 + 1; // 7
  private const uint BottomValue = TopValue >> 8;     // 1<<23

  // The 32-bit-word bit array (reference layout: each word's bytes are MSB-first).
  private readonly uint[] _words;
  private int _bitIndex;

  private uint _low;
  private uint _range;
  private uint _buffer;

  /// <summary>Wraps a frame payload. <paramref name="data"/>/<paramref name="offset"/>/
  /// <paramref name="length"/> describe the raw on-disk bytes (length is rounded up to
  /// a multiple of four, matching the reference's word-aligned frames).</summary>
  public ApeRangeDecoder(byte[] data, int offset, int length) {
    var words = (length + 3) / 4;
    this._words = new uint[words + 1]; // +1 guard word for tail reads
    for (var i = 0; i < words; ++i) {
      // The reference bit array is a uint32[] whose cells are addressed MSB-first
      // (PUTC writes byte N at bit-shift 24-(idx&31)). It is written to disk as the
      // raw uint32 array on a little-endian host, so on disk each four-byte group is
      // the MSB-first word in little-endian byte order. Reassemble accordingly.
      var b0 = (uint)(offset + i * 4 + 0 < offset + length ? data[offset + i * 4 + 0] : 0);
      var b1 = (uint)(offset + i * 4 + 1 < offset + length ? data[offset + i * 4 + 1] : 0);
      var b2 = (uint)(offset + i * 4 + 2 < offset + length ? data[offset + i * 4 + 2] : 0);
      var b3 = (uint)(offset + i * 4 + 3 < offset + length ? data[offset + i * 4 + 3] : 0);
      this._words[i] = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }
    this._bitIndex = 0;
  }

  private uint Word(int index) => index >= 0 && index < this._words.Length ? this._words[index] : 0u;

  private uint NextByte() {
    var b = (this.Word(this._bitIndex >> 5) >> (24 - (this._bitIndex & 31))) & 0xFF;
    this._bitIndex += 8;
    return b;
  }

  /// <summary>Reads a raw 32-bit big-endian word (used for the per-frame CRC).</summary>
  public uint DecodeUnsignedInt() {
    var leftBits = 32 - (this._bitIndex & 31);
    var wordIndex = this._bitIndex >> 5;
    this._bitIndex += 32;
    if (leftBits == 32)
      return this.Word(wordIndex);
    var hi = this.Word(wordIndex) << (32 - leftBits);
    var lo = this.Word(wordIndex + 1) >> leftBits;
    return hi | lo;
  }

  /// <summary>Aligns to a byte boundary then primes the range coder by ignoring the
  /// first byte (reference <c>FlushBitArray</c>).</summary>
  public void StartDecoding() {
    if ((this._bitIndex & 7) != 0)
      this._bitIndex += 8 - (this._bitIndex & 7);
    this._bitIndex += 8; // ignore the first (dummy) byte
    this._buffer = this.NextByte();
    this._low = this._buffer >> (8 - ExtraBits);
    this._range = 1u << ExtraBits;
  }

  private void Normalize() {
    while (this._range <= BottomValue) {
      this._buffer = (this._buffer << 8) | this.NextByte();
      this._low = (this._low << 8) | ((this._buffer >> 1) & 0xFF);
      this._range <<= 8;
    }
  }

  /// <summary>Reference <c>RangeDecodeFast</c>: normalise, divide range by
  /// 2^shift, return <c>low / range</c> WITHOUT updating.</summary>
  public uint DecodeFast(int shift) {
    this.Normalize();
    this._range >>= shift;
    return this._low / this._range;
  }

  /// <summary>Reference <c>RangeDecodeFastWithUpdate</c>: like <see cref="DecodeFast"/>
  /// but consumes the decoded symbol.</summary>
  public uint DecodeFastWithUpdate(int shift) {
    this.Normalize();
    this._range >>= shift;
    var ret = this._low / this._range;
    this._low -= this._range * ret;
    return ret;
  }

  /// <summary>Reference <c>range/pivot</c> base read: normalise, divide range by an
  /// arbitrary divisor, return and consume <c>low / range</c>.</summary>
  public uint DecodeByDivisor(uint divisor) {
    this.Normalize();
    this._range /= divisor;
    var ret = this._low / this._range;
    this._low -= this._range * ret;
    return ret;
  }

  /// <summary>Consumes an overflow-class cell selected by the cumulative table.</summary>
  public void UpdateOverflow(uint total, uint width) {
    this._low -= this._range * total;
    this._range *= width;
  }
}

/// <summary>
/// 32-bit range encoder — a byte-exact port of the reference SDK's <c>CBitArray</c>
/// range coder (<c>NORMALIZE_RANGE_CODER</c>, <c>ENCODE_FAST</c>, <c>ENCODE_DIRECT</c>,
/// <c>Finalize</c>). It writes the same big-endian-within-words bit array the
/// reference produces, so a frame this encoder emits is byte-identical to what the
/// reference Monkey's Audio encoder would write for the same residual stream and
/// decodes through <see cref="ApeRangeDecoder"/> (and through ffmpeg).
/// </summary>
internal sealed class ApeRangeEncoder {
  private const int CodeBits = 32;
  private const uint TopValue = 1u << (CodeBits - 1); // 1<<31
  private const int ShiftBits = CodeBits - 9;         // 23
  private const uint BottomValue = TopValue >> 8;     // 1<<23

  private readonly List<uint> _words = [0];
  private int _bitIndex;

  private uint _low;
  private uint _range = TopValue;
  private uint _buffer;
  private uint _help; // bytes_to_follow

  private void EnsureWord(int wordIndex) {
    while (this._words.Count <= wordIndex + 1)
      this._words.Add(0);
  }

  private void Putc(uint value) {
    var wi = this._bitIndex >> 5;
    this.EnsureWord(wi);
    this._words[wi] |= (value & 0xFF) << (24 - (this._bitIndex & 31));
    this._bitIndex += 8;
  }

  private void Normalize() {
    while (this._range <= BottomValue) {
      if (this._low < (0xFFu << ShiftBits)) {
        this.Putc(this._buffer);
        for (; this._help != 0; --this._help)
          this.Putc(0xFF);
        this._buffer = this._low >> ShiftBits;
      } else if ((this._low & TopValue) != 0) {
        this.Putc(this._buffer + 1);
        this._bitIndex += (int)(this._help * 8);
        this.EnsureWord(this._bitIndex >> 5);
        this._help = 0;
        this._buffer = this._low >> ShiftBits;
      } else {
        ++this._help;
      }

      this._low = (this._low << 8) & (TopValue - 1);
      this._range <<= 8;
    }
  }

  /// <summary>Reference <c>ENCODE_FAST</c>: encode an overflow-class cell.</summary>
  public void EncodeFast(uint width, uint total, int shift) {
    this.Normalize();
    var temp = this._range >> shift;
    this._range = temp * width;
    this._low += temp * total;
  }

  /// <summary>Reference <c>ENCODE_DIRECT</c>: encode a raw value over 2^shift.</summary>
  public void EncodeDirect(uint value, int shift) {
    this.Normalize();
    this._range >>= shift;
    this._low += this._range * value;
  }

  /// <summary>Reference base encode by an arbitrary pivot divisor.</summary>
  public void EncodeByDivisor(uint value, uint divisor) {
    this.Normalize();
    this._range /= divisor;
    this._low += this._range * value;
  }

  /// <summary>Reference <c>EncodeUnsignedLong</c>: write a raw 32-bit big-endian word
  /// (used for the per-frame CRC).</summary>
  public void EncodeUnsignedInt(uint value) {
    var wi = this._bitIndex >> 5;
    var bit = this._bitIndex & 31;
    this.EnsureWord(wi + 1);
    if (bit == 0) {
      this._words[wi] = value;
    } else {
      this._words[wi] |= value >> bit;
      this._words[wi + 1] = value << (32 - bit);
    }
    this._bitIndex += 32;
  }

  /// <summary>Aligns to a byte boundary then resets the range coder state
  /// (reference <c>FlushBitArray</c>).</summary>
  public void FlushBitArray() {
    if ((this._bitIndex & 7) != 0)
      this._bitIndex += 8 - (this._bitIndex & 7);
    this._low = 0;
    this._range = TopValue;
    this._buffer = 0;
    this._help = 0;
  }

  /// <summary>Reference <c>Finalize</c>: flush the residual range-coder state.</summary>
  public void FinalizeStream() {
    this.Normalize();
    var temp = (this._low >> ShiftBits) + 1;
    if (temp > 0xFF) {
      this.Putc(this._buffer + 1);
      for (; this._help != 0; --this._help)
        this.Putc(0);
    } else {
      this.Putc(this._buffer);
      for (; this._help != 0; --this._help)
        this.Putc(0xFF);
    }
    this.Putc(temp & 0xFF);
    this.Putc(0);
    this.Putc(0);
    this.Putc(0);
  }

  /// <summary>Serialises the finished bit array to on-disk bytes: each 32-bit
  /// MSB-first word emitted in little-endian byte order (the raw layout the
  /// reference writes on a little-endian host), truncated to the byte length the
  /// reference would write (<c>(bitIndex&gt;&gt;5)*4 + 4</c>).</summary>
  public byte[] ToArray() {
    var byteLength = ((this._bitIndex >> 5) * 4) + 4;
    var outBuf = new byte[byteLength];
    for (var i = 0; i < byteLength; i += 4) {
      var w = this._words[i >> 2];
      outBuf[i] = (byte)w;
      if (i + 1 < byteLength) outBuf[i + 1] = (byte)(w >> 8);
      if (i + 2 < byteLength) outBuf[i + 2] = (byte)(w >> 16);
      if (i + 3 < byteLength) outBuf[i + 3] = (byte)(w >> 24);
    }
    return outBuf;
  }
}
