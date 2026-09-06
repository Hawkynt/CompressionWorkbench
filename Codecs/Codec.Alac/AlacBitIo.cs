#pragma warning disable CS1591

namespace Codec.Alac;

/// <summary>
/// MSB-first big-endian bit reader, the equivalent of the reference <c>BitBuffer</c>
/// (<c>ALACBitUtilities.c</c>). ALAC packs every field — element tags, prediction
/// headers, matrix parameters and the adaptive Golomb/Rice residual codes — as a
/// big-endian bit stream, so all reads pull from the high bit of the current byte
/// downward.
/// <para>
/// The adaptive Golomb decoder needs three operations beyond a plain read: peeking a
/// 32-bit window to count the unary prefix, advancing by a computed number of bits,
/// and stepping <em>back</em> one bit when a Rice suffix turns out to be shorter than
/// the peeked width. Reads past the end of the buffer yield zero bits rather than
/// throwing, matching the reference, which reads a 32-bit word regardless of where
/// the frame ends.
/// </para>
/// </summary>
internal sealed class AlacBitReader {
  private readonly byte[] _data;
  private readonly int _start;
  private readonly int _end;
  private int _bitIndex; // absolute bit offset into _data

  public AlacBitReader(byte[] data, int start, int length) {
    this._data = data;
    this._start = start;
    this._end = start + length;
    this._bitIndex = start * 8;
  }

  private AlacBitReader(AlacBitReader other) {
    this._data = other._data;
    this._start = other._start;
    this._end = other._end;
    this._bitIndex = other._bitIndex;
  }

  /// <summary>Creates an independent cursor at the current position over the same buffer.</summary>
  public AlacBitReader Clone() => new(this);

  /// <summary>Total bits consumed since construction.</summary>
  public int Position => this._bitIndex - this._start * 8;

  /// <summary>True once the cursor has passed the end of the buffer.</summary>
  public bool Exhausted => this._bitIndex >= this._end * 8;

  /// <summary>Peeks the next <paramref name="count"/> bits (0..32) without advancing.</summary>
  public uint Peek(int count) {
    if (count <= 0)
      return 0;

    var byteIndex = this._bitIndex >> 3;
    var bitOffset = this._bitIndex & 7;
    var needed = (count + bitOffset + 7) >> 3; // at most 5

    ulong window = 0;
    for (var i = 0; i < needed; ++i) {
      var p = byteIndex + i;
      window = (window << 8) | (p >= this._start && p < this._end ? this._data[p] : 0u);
    }

    window >>= needed * 8 - bitOffset - count;
    return count >= 32 ? (uint)window : (uint)(window & ((1UL << count) - 1));
  }

  /// <summary>Reads <paramref name="count"/> bits (0..32) MSB-first into the low bits of the result.</summary>
  public uint Read(int count) {
    var value = this.Peek(count);
    this._bitIndex += count;
    return value;
  }

  /// <summary>Reads a single bit.</summary>
  public uint ReadOne() => this.Read(1);

  /// <summary>Advances the cursor by <paramref name="count"/> bits; negative values step back.</summary>
  public void Advance(int count) => this._bitIndex += count;

  /// <summary>Aligns the cursor to the next byte boundary.</summary>
  public void ByteAlign() {
    var slack = this._bitIndex & 7;
    if (slack != 0)
      this._bitIndex += 8 - slack;
  }
}

/// <summary>
/// MSB-first big-endian bit writer mirroring the reader. Used by the encoder to
/// emit spec-shaped ALAC frames. Bits accumulate into a growable byte list; a final
/// <see cref="ToArray"/> flushes the partially filled trailing byte (low bits zero),
/// exactly as the reference encoder leaves the unused tail of a frame zero-padded.
/// </summary>
internal sealed class AlacBitWriter {
  private readonly List<byte> _bytes = [];
  private int _current;
  private int _bitsFilled; // 0..7 in the current byte.

  /// <summary>Total bits written so far.</summary>
  public int Position => this._bytes.Count * 8 + this._bitsFilled;

  /// <summary>Writes the low <paramref name="count"/> bits (0..32) of <paramref name="value"/> MSB-first.</summary>
  public void Write(uint value, int count) {
    for (var i = count - 1; i >= 0; --i) {
      var bit = (int)((value >> i) & 1);
      this._current = (this._current << 1) | bit;
      if (++this._bitsFilled != 8)
        continue;
      this._bytes.Add((byte)this._current);
      this._current = 0;
      this._bitsFilled = 0;
    }
  }

  /// <summary>Writes a single bit.</summary>
  public void WriteOne(uint value) => this.Write(value & 1, 1);

  /// <summary>Flushes any partial byte (zero-padded low bits) and returns the bytes.</summary>
  public byte[] ToArray() {
    if (this._bitsFilled <= 0)
      return [.. this._bytes];

    var padded = this._current << (8 - this._bitsFilled);
    var copy = new List<byte>(this._bytes) { (byte)padded };
    return [.. copy];
  }
}
