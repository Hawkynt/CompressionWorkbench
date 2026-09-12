#pragma warning disable CS1591

namespace Codec.WavPack;

/// <summary>
/// Bit reader matching WavPack's <c>bits.c</c>: the very first bit consumed from
/// the bitstream is bit 0 (value 1) of the first byte, proceeding to bit 1,
/// bit 2, … i.e. least-significant-bit-first within each byte. The reference
/// reads whole bytes into a 32-bit accumulator and shifts them out from the low
/// end; this reader reproduces that LSB-first-per-byte order one bit at a time,
/// which is all the WavPack word coder needs.
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
/// within each byte and spills complete bytes to the output. It additionally owns
/// the reference encoder's <c>pend_data</c>/<c>pend_count</c> tail accumulator so
/// the cross-word <c>flush_word</c> ordering (zero-run, held ones, held zero,
/// pending tail/sign) can be reproduced bit-for-bit. <see cref="Flush"/> pads the
/// trailing partial byte with one bits (matching the reference's end-of-stream
/// convention) and returns the coded bytes.
/// </summary>
internal sealed class WavPackBitWriter {
  private readonly List<byte> _bytes = [];
  private int _bitBuffer;
  private int _bitCount;

  // Reference "pend_data"/"pend_count": tail+sign bits held until flush_word.
  private uint _pendData;
  private int _pendCount;

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

  /// <summary>Appends one bit to the pending tail accumulator (reference
  /// <c>pend_data |= bit &lt;&lt; pend_count++</c>).</summary>
  public void PutPendBit(int bit) {
    this._pendData |= (uint)(bit & 1) << this._pendCount;
    ++this._pendCount;
  }

  /// <summary>Appends <paramref name="count"/> low bits of <paramref name="value"/>
  /// to the pending tail accumulator.</summary>
  public void PutPendBits(uint value, int count) {
    this._pendData |= (value & ((count >= 32 ? 0u : 1u << count) - 1)) << this._pendCount;
    this._pendCount += count;
  }

  /// <summary>Spills the pending tail/sign bits onto the bitstream, the final step
  /// of the reference <c>flush_word</c>. The unused parameter keeps the call site
  /// reading like the reference (<c>flush_word(wps)</c>).</summary>
  public void FlushPending(WavPackBitWriter _) {
    if (this._pendCount == 0)
      return;

    this.PutBits(this._pendData, this._pendCount);
    this._pendData = 0;
    this._pendCount = 0;
  }

  public byte[] Flush() {
    if (this._bitCount > 0)
      // Pad the final byte with one bits, as the reference does at EOS.
      while (this._bitCount != 0)
        this.PutBit(1);

    return this._bytes.ToArray();
  }

  /// <summary>Flushes like <see cref="Flush"/> but additionally pads the byte count
  /// to an even length with a trailing one-bit byte, matching the reference
  /// <c>bs_close_write</c> used for the wvx extension bitstream (whose sub-block
  /// payload must be 16-bit aligned).</summary>
  public byte[] FlushEven() {
    while (this._bitCount != 0)
      this.PutBit(1);
    if ((this._bytes.Count & 1) != 0)
      this._bytes.Add(0xFF); // pad to even, as the reference fills with one-bits
    return this._bytes.ToArray();
  }
}
