#pragma warning disable CS1591
namespace Codec.CriHca;

/// <summary>
/// Most-significant-bit-first bit reader matching FFmpeg's <c>get_bits.h</c> (the HCA
/// bitstream is big-endian). Reads from a byte buffer; reads past the end return zero
/// bits so a truncated final frame still decodes deterministically. A negative
/// argument to <see cref="Skip"/> rewinds — needed by the HCA coefficient dequantiser,
/// which un-reads a single bit (<c>skip_bits_long(gb, -1)</c>) when a sign/magnitude
/// value decodes to zero.
/// </summary>
internal sealed class HcaBitReader {
  private readonly byte[] _data;
  private readonly int _offset;
  private readonly int _lengthBits;
  private int _bitPos;

  public HcaBitReader(byte[] data, int offset, int lengthBytes) {
    this._data = data;
    this._offset = offset;
    this._lengthBits = lengthBytes * 8;
    this._bitPos = 0;
  }

  /// <summary>Current bit position (for limit checks / diagnostics).</summary>
  public int Position => this._bitPos;

  /// <summary>Reads <paramref name="count"/> bits (0–32) MSB first; past EOF yields zeros.</summary>
  public int GetBits(int count) {
    var value = 0;
    for (var i = 0; i < count; ++i) {
      var bitIndex = this._bitPos;
      var bit = 0;
      if (bitIndex >= 0 && bitIndex < this._lengthBits) {
        var byteIndex = this._offset + (bitIndex >> 3);
        if (byteIndex >= 0 && byteIndex < this._data.Length)
          bit = (this._data[byteIndex] >> (7 - (bitIndex & 7))) & 1;
      }
      value = (value << 1) | bit;
      ++this._bitPos;
    }
    return value;
  }

  /// <summary>Reads a single bit.</summary>
  public int GetBit() => this.GetBits(1);

  /// <summary>
  /// Reads <paramref name="count"/> bits, returning 0 (and consuming nothing) when
  /// <paramref name="count"/> is 0 — mirrors FFmpeg's <c>get_bitsz</c>.
  /// </summary>
  public int GetBitsZ(int count) => count == 0 ? 0 : this.GetBits(count);

  /// <summary>Advances (or, for a negative <paramref name="count"/>, rewinds) the bit cursor.</summary>
  public void Skip(int count) => this._bitPos += count;
}
