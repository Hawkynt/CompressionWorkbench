#pragma warning disable CS1591

namespace Codec.WavPack;

/// <summary>
/// Big-endian-within-byte bit reader matching WavPack's <c>bits.c</c>: the very
/// first bit consumed from the bitstream is bit 0 (value 1) of the first byte,
/// proceeding to bit 1, bit 2, … i.e. least-significant-bit-first within each
/// byte. The reference reads whole bytes into a 32-bit accumulator and shifts
/// them out from the low end; this reader reproduces that LSB-first-per-byte
/// order one bit at a time, which is all the WavPack word coder needs.
/// </summary>
internal sealed class WavPackBitReader {
  private readonly byte[] _data;
  private readonly int _end;
  private int _pos;
  private int _bitBuffer;
  private int _bitsAvailable;

  public WavPackBitReader(byte[] data, int offset, int length) {
    this._data = data;
    this._pos = offset;
    this._end = offset + length;
  }

  /// <summary>Reads a single bit (0 or 1), LSB-first within each byte. Past the
  /// end of the buffer the stream reads as all-ones, exactly like the reference
  /// (which lets a truncated final block terminate cleanly).</summary>
  public int GetBit() {
    if (this._bitsAvailable == 0) {
      this._bitBuffer = this._pos < this._end ? this._data[this._pos++] : 0xFF;
      this._bitsAvailable = 8;
    }
    var bit = this._bitBuffer & 1;
    this._bitBuffer >>= 1;
    --this._bitsAvailable;
    return bit;
  }

  /// <summary>Reads <paramref name="count"/> bits (0–32) LSB-first and returns them
  /// with the first-read bit in position 0.</summary>
  public uint GetBits(int count) {
    var value = 0u;
    for (var i = 0; i < count; ++i)
      value |= (uint)this.GetBit() << i;
    return value;
  }
}

/// <summary>
/// The exact inverse of <see cref="WavPackBitReader"/>: accumulates bits LSB-first
/// within each byte and spills complete bytes to the output. <see cref="Flush"/>
/// pads the trailing partial byte with one bits (matching the reference's
/// end-of-stream convention) and returns the coded bytes.
/// </summary>
internal sealed class WavPackBitWriter {
  private readonly List<byte> _bytes = [];
  private int _bitBuffer;
  private int _bitCount;

  public void PutBit(int bit) {
    this._bitBuffer |= (bit & 1) << this._bitCount;
    if (++this._bitCount != 8)
      return;
    this._bytes.Add((byte)this._bitBuffer);
    this._bitBuffer = 0;
    this._bitCount = 0;
  }

  public void PutBits(uint value, int count) {
    for (var i = 0; i < count; ++i)
      this.PutBit((int)((value >> i) & 1));
  }

  public byte[] Flush() {
    if (this._bitCount > 0) {
      // Pad the final byte with one bits, as the reference does at EOS.
      while (this._bitCount != 0)
        this.PutBit(1);
    }
    return this._bytes.ToArray();
  }
}
