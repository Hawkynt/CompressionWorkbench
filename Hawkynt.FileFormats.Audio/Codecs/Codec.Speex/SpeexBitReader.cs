#pragma warning disable CS1591

namespace Codec.Speex;

/// <summary>
/// MSB-first bit reader matching FFmpeg's <c>get_bits</c> family as used by the
/// Speex decoder. <see cref="GetBits"/> reads bits big-endian;
/// <see cref="GetBitsZ"/> returns 0 for a zero-width request (mirrors
/// <c>get_bitsz</c>); reads past the end of the buffer yield zero bits, matching the
/// reference behaviour for truncated packets.
/// </summary>
internal sealed class SpeexBitReader {
  private readonly byte[] _data;
  private readonly int _length;
  private int _bitPos;

  public SpeexBitReader(byte[] data, int byteLength) {
    this._data = data;
    this._length = byteLength;
  }

  /// <summary>Total bits in the buffer.</summary>
  public int TotalBits => this._length * 8;

  /// <summary>Bits remaining (can go negative conceptually; clamped at 0).</summary>
  public int BitsLeft => Math.Max(0, this.TotalBits - this._bitPos);

  /// <summary>Number of bits already consumed.</summary>
  public int BitsRead => this._bitPos;

  public int GetBits(int n) {
    var value = 0;
    for (var i = 0; i < n; ++i) {
      var byteIndex = this._bitPos >> 3;
      var bit = byteIndex < this._length
        ? (this._data[byteIndex] >> (7 - (this._bitPos & 7))) & 1
        : 0;
      value = (value << 1) | bit;
      ++this._bitPos;
    }
    return value;
  }

  public int GetBitsZ(int n) => n <= 0 ? 0 : this.GetBits(n);

  public int GetBits1() => this.GetBits(1);

  /// <summary>Peek the next bit without consuming (mirrors <c>show_bits1</c>).</summary>
  public int ShowBits1() {
    var byteIndex = this._bitPos >> 3;
    return byteIndex < this._length
      ? (this._data[byteIndex] >> (7 - (this._bitPos & 7))) & 1
      : 0;
  }

  /// <summary>Peek <paramref name="n"/> bits without consuming (mirrors <c>show_bits</c>).</summary>
  public int ShowBits(int n) {
    var save = this._bitPos;
    var v = this.GetBits(n);
    this._bitPos = save;
    return v;
  }

  public void SkipBits(int n) => this._bitPos += n;
}
