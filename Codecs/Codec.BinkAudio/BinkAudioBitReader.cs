#pragma warning disable CS1591
namespace Codec.BinkAudio;

/// <summary>
/// LSB-first bit reader matching FFmpeg's <c>BITSTREAM_READER_LE</c> <c>get_bits</c>
/// (binkaudio.c defines <c>BITSTREAM_READER_LE</c>). Within each byte bit 0 is read first
/// and multi-bit fields place the least-significant bit of the first byte in the
/// least-significant position of the result. The reader is initialised with an explicit
/// bit length (<c>init_get_bits8</c> with a byte buffer); reads past the declared end
/// return zero bits while still advancing the position, mirroring how the reference
/// tolerates an over-read at the tail of a packet.
/// </summary>
internal sealed class BinkAudioBitReader {
  private readonly byte[] _data;
  private readonly int _offset;
  private readonly int _sizeInBits;
  private int _index;

  public BinkAudioBitReader(byte[] data, int offset, int sizeInBits) {
    this._data = data;
    this._offset = offset;
    this._sizeInBits = sizeInBits;
    this._index = 0;
  }

  /// <summary>Number of bits consumed so far (<c>get_bits_count</c>).</summary>
  public int BitsCount => this._index;

  /// <summary>Number of bits still available (<c>get_bits_left</c>); may go negative on over-read.</summary>
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

  /// <summary>
  /// Reads <paramref name="n"/> bits (0–32) LSB-first (<c>get_bits</c>/<c>get_bits_long</c>):
  /// the first bit read becomes the least-significant bit of the result.
  /// </summary>
  public uint GetBits(int n) {
    uint value = 0;
    for (var i = 0; i < n; ++i)
      value |= (uint)this.GetBit() << i;
    return value;
  }

  /// <summary>Skips <paramref name="n"/> bits (<c>skip_bits</c>).</summary>
  public void SkipBits(int n) => this._index += n;

  /// <summary>Aligns the bit position up to the next 32-bit boundary (<c>get_bits_align32</c>).</summary>
  public void Align32() {
    var n = (-this._index) & 31;
    if (n != 0)
      this._index += n;
  }
}
