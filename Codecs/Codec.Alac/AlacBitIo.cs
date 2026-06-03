#pragma warning disable CS1591

namespace Codec.Alac;

/// <summary>
/// MSB-first big-endian bit reader, a direct analogue of Apple's <c>BitBuffer</c>
/// (<c>ALACBitUtilities.c</c>). ALAC packs every field — element tags, prediction
/// headers, matrix parameters and the adaptive Golomb/Rice residual codes — as a
/// big-endian bit stream, so all reads pull from the high bit of the current byte
/// downward. The reader keeps a running fractional bit position and never reads
/// past the supplied buffer (returning zero-filled bits at the end), matching the
/// reference behaviour where the caller guarantees the frame is well-formed.
/// </summary>
internal sealed class AlacBitReader {
  private readonly byte[] _data;
  private readonly int _start;
  private readonly int _end;
  private int _bytePos;
  private int _bitPos; // 0..7, counted from the MSB.

  public AlacBitReader(byte[] data, int start, int length) {
    this._data = data;
    this._start = start;
    this._bytePos = start;
    this._end = start + length;
    this._bitPos = 0;
  }

  /// <summary>Total bits consumed since construction.</summary>
  public int Position => (this._bytePos - this._start) * 8 + this._bitPos;

  /// <summary>Reads <paramref name="count"/> bits (0..32) MSB-first into the low bits of the result.</summary>
  public uint Read(int count) {
    var result = 0u;
    for (var i = 0; i < count; ++i) {
      var bit = 0u;
      if (this._bytePos < this._end)
        bit = (uint)((this._data[this._bytePos] >> (7 - this._bitPos)) & 1);
      result = (result << 1) | bit;
      if (++this._bitPos != 8)
        continue;
      this._bitPos = 0;
      ++this._bytePos;
    }
    return result;
  }

  /// <summary>Reads a single bit.</summary>
  public uint ReadOne() => this.Read(1);

  /// <summary>Peeks the next <paramref name="count"/> bits (0..32) without advancing.</summary>
  public uint Peek(int count) {
    var savedByte = this._bytePos;
    var savedBit = this._bitPos;
    var result = this.Read(count);
    this._bytePos = savedByte;
    this._bitPos = savedBit;
    return result;
  }

  /// <summary>Advances the cursor by <paramref name="count"/> bits.</summary>
  public void Advance(int count) {
    var total = this._bitPos + count;
    this._bytePos += total >> 3;
    this._bitPos = total & 7;
  }

  /// <summary>Aligns the cursor to the next byte boundary.</summary>
  public void ByteAlign() {
    if (this._bitPos == 0)
      return;
    this._bitPos = 0;
    ++this._bytePos;
  }

  /// <summary>Byte position of the cursor when byte-aligned (rounds a partial byte up).</summary>
  public int BytePositionRoundedUp => this._bitPos == 0 ? this._bytePos : this._bytePos + 1;
}

/// <summary>
/// MSB-first big-endian bit writer mirroring the reader. Used by the encoder to
/// emit spec-shaped ALAC frames. Bits accumulate into a growable byte list; a final
/// <see cref="ToArray"/> flushes the partially filled trailing byte (low bits zero),
/// exactly as Apple's encoder leaves the unused tail of a frame zero-padded.
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
      return this._bytes.ToArray();

    var padded = this._current << (8 - this._bitsFilled);
    var copy = new List<byte>(this._bytes) { (byte)padded };
    return copy.ToArray();
  }
}
