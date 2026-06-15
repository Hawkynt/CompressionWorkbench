#pragma warning disable CS1591
namespace Codec.SmackerAudio;

/// <summary>
/// LSB-first bit reader matching FFmpeg's <c>BITSTREAM_READER_LE</c> <c>get_bits</c>
/// (smacker.c defines <c>BITSTREAM_READER_LE</c>). Within each byte bit 0 is read first and
/// multi-bit fields place the least-significant bit of the first byte in the
/// least-significant position of the result. Reads past the declared end return zero bits
/// while still advancing the position, and <see cref="BitsLeft"/> goes negative on
/// over-read so the caller can reproduce the reference's <c>get_bits_left() &lt; 0</c>
/// bail-outs.
/// </summary>
internal sealed class SmackerBitReader {
  private readonly byte[] _data;
  private readonly int _offset;
  private readonly int _sizeInBits;
  private int _index;

  public SmackerBitReader(byte[] data, int offset, int lengthBytes) {
    this._data = data;
    this._offset = offset;
    this._sizeInBits = lengthBytes * 8;
    this._index = 0;
  }

  /// <summary>Bits still available (<c>get_bits_left</c>); negative once the reader over-reads.</summary>
  public int BitsLeft => this._sizeInBits - this._index;

  /// <summary>Reads a single bit LSB-first (<c>get_bits1</c>).</summary>
  public int GetBit() {
    var bit = 0;
    var bitIndex = this._index;
    var bytePos = this._offset + (bitIndex >> 3);
    if (bitIndex < this._sizeInBits && bytePos < this._data.Length)
      bit = (this._data[bytePos] >> (bitIndex & 7)) & 1;
    ++this._index;
    return bit;
  }

  /// <summary>Skips a single bit (<c>skip_bits1</c>).</summary>
  public void SkipBit() => ++this._index;

  /// <summary>Reads <paramref name="n"/> bits (0–32) LSB-first (<c>get_bits</c>).</summary>
  public uint GetBits(int n) {
    uint value = 0;
    for (var i = 0; i < n; ++i)
      value |= (uint)this.GetBit() << i;
    return value;
  }
}
