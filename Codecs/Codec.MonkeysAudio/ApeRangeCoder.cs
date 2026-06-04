#pragma warning disable CS1591

namespace Codec.MonkeysAudio;

/// <summary>
/// Shared constants for Monkey's Audio's range coder, taken from the reference
/// SDK / ffmpeg <c>libavcodec/apedec.c</c>. <see cref="Counts"/> is the 64-entry
/// cumulative-frequency table the "current" (3.98+) entropy stage uses to map a
/// scaled range cell to an overflow class, and <see cref="Widths"/> the matching
/// per-class frequencies; the total is <see cref="Total"/> (1&lt;&lt;16).
/// </summary>
internal static class ApeRangeConstants {

  public const uint Total = 1u << 16; // 65536

  // ffmpeg apedec.c counts_3980[]: cumulative frequencies for the overflow class.
  public static readonly uint[] Counts = [
        0, 19578, 36160, 48417, 56323,
    60899, 63265, 64435, 64971, 65232,
    65351, 65416, 65447, 65466, 65476,
    65482, 65485, 65488, 65490, 65491,
    65492, 65493, 65494, 65495, 65496,
    65497, 65498, 65499, 65500, 65501,
    65502, 65503, 65504, 65505, 65506,
    65507, 65508, 65509, 65510, 65511,
    65512, 65513, 65514, 65515, 65516,
    65517, 65518, 65519, 65520, 65521,
    65522, 65523, 65524, 65525, 65526,
    65527, 65528, 65529, 65530, 65531,
    65532, 65533, 65534, 65535,
  ];

  // ffmpeg apedec.c counts_diff_3980[]: per-class frequency = Counts[i+1]-Counts[i].
  public static readonly uint[] Widths = BuildWidths();

  private static uint[] BuildWidths() {
    var widths = new uint[Counts.Length];
    for (var i = 0; i < Counts.Length - 1; ++i)
      widths[i] = Counts[i + 1] - Counts[i];
    widths[^1] = Total - Counts[^1];
    return widths;
  }

  /// <summary>Maps a cumulative-frequency value in [0, 65536) to its overflow
  /// class via the <see cref="Counts"/> table.</summary>
  public static int ClassForCumulative(uint cf) {
    var overflow = 0;
    while (overflow < Counts.Length - 1 && cf >= Counts[overflow + 1])
      ++overflow;
    return overflow;
  }
}

/// <summary>
/// 32-bit range decoder for Monkey's Audio frames, using the byte-oriented
/// carry-propagating normalisation of the LZMA/PPMd range-coder family (top =
/// 1&lt;&lt;24, big-endian byte refill). This keeps the same low/range cell model
/// as the reference SDK / ffmpeg <c>apedec.c</c> entropy stage while using carry
/// handling robust for arbitrary frequency models. Symbols are read either as
/// cumulative-frequency cells (<see cref="DecodeFrequency"/> +
/// <see cref="DecodeUpdate"/>) or as raw bit fields (<see cref="DecodeBits"/>).
/// The matching <see cref="ApeRangeEncoder"/> is its exact algebraic inverse, so
/// a stream this codec writes round-trips bit-for-bit.
/// </summary>
internal sealed class ApeRangeDecoder {
  private const uint Top = 1u << 24;

  private readonly byte[] _data;
  private int _pos;
  private readonly int _end;
  private uint _range;
  private uint _code;

  public ApeRangeDecoder(byte[] data, int offset, int length) {
    this._data = data;
    this._pos = offset;
    this._end = offset + length;
    this._range = 0xFFFFFFFF;
    this._code = 0;
    // Skip the encoder's leading priming byte, then load four code bytes.
    this.NextByte();
    for (var i = 0; i < 4; ++i)
      this._code = (this._code << 8) | this.NextByte();
  }

  private uint NextByte() => (uint)(this._pos < this._end ? this._data[this._pos++] : 0);

  /// <summary>Returns the cumulative-frequency cell of the current symbol for a
  /// model with total <paramref name="total"/>.</summary>
  public uint DecodeFrequency(uint total) {
    this._range /= total;
    var cf = this._code / this._range;
    return cf >= total ? total - 1 : cf;
  }

  /// <summary>Narrows the cell to the symbol spanning [start, start+width) of the
  /// total used in the preceding <see cref="DecodeFrequency"/>, then renormalises.</summary>
  public void DecodeUpdate(uint start, uint width) {
    this._code -= start * this._range;
    this._range *= width;
    this.Normalize();
  }

  /// <summary>Reads <paramref name="bits"/> raw bits (0–32) as a uniform field.</summary>
  public uint DecodeBits(int bits) {
    switch (bits) {
      case 0:
        return 0;
      case >= 32: {
        var hi = this.DecodeBits(16);
        var lo = this.DecodeBits(16);
        return (hi << 16) | lo;
      }
      default: {
        var total = 1u << bits;
        var value = this.DecodeFrequency(total);
        this.DecodeUpdate(value, 1);
        return value;
      }
    }
  }

  private void Normalize() {
    while (this._range < Top) {
      this._code = (this._code << 8) | this.NextByte();
      this._range <<= 8;
    }
  }
}

/// <summary>
/// The exact algebraic inverse of <see cref="ApeRangeDecoder"/>: a 32-bit range
/// encoder with the carry-counting (cache / cache-size) spill of the LZMA/PPMd
/// range-coder family. For any symbol the decoder would read it emits precisely
/// the bytes that reproduce it, so a stream this codec writes round-trips
/// bit-for-bit through the decoder. Symbols are submitted as (start, width, total)
/// cells matching <see cref="ApeRangeDecoder.DecodeFrequency"/>/
/// <see cref="ApeRangeDecoder.DecodeUpdate"/>, or as raw bit fields matching
/// <see cref="ApeRangeDecoder.DecodeBits"/>.
/// </summary>
internal sealed class ApeRangeEncoder {
  private const uint Top = 1u << 24;

  private readonly List<byte> _out = [];
  private ulong _low;
  private uint _range = 0xFFFFFFFF;
  private byte _cache;
  private long _cacheSize = 1;

  /// <summary>Encodes a symbol spanning [start, start+width) of <paramref name="total"/>.</summary>
  public void EncodeCell(uint start, uint width, uint total) {
    this._range /= total;
    this._low += start * this._range;
    this._range *= width;
    this.Normalize();
  }

  /// <summary>Encodes <paramref name="value"/> as <paramref name="bits"/> raw bits.</summary>
  public void EncodeBits(uint value, int bits) {
    switch (bits) {
      case 0:
        return;
      case >= 32:
        this.EncodeBits(value >> 16, 16);
        this.EncodeBits(value & 0xFFFF, 16);
        return;
      default:
        this.EncodeCell(value, 1, 1u << bits);
        return;
    }
  }

  private void Normalize() {
    while (this._range < Top) {
      this.ShiftLow();
      this._range <<= 8;
    }
  }

  private void ShiftLow() {
    if (this._low < 0xFF000000UL || this._low > 0xFFFFFFFFUL) {
      var carry = (byte)(this._low >> 32);
      do {
        this._out.Add((byte)(this._cache + carry));
        this._cache = 0xFF;
      } while (--this._cacheSize != 0);
      this._cache = (byte)(this._low >> 24);
    }
    ++this._cacheSize;
    this._low = (this._low << 8) & 0xFFFFFFFFUL;
  }

  /// <summary>Flushes the residual <c>low</c> and returns the frame payload.</summary>
  public byte[] Finish() {
    for (var i = 0; i < 5; ++i)
      this.ShiftLow();
    return this._out.ToArray();
  }
}
