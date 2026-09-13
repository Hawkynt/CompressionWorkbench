#pragma warning disable CS1591
namespace Codec.Atrac3;

/// <summary>
/// Most-significant-bit-first bit reader matching FFmpeg's default <c>get_bits.h</c>
/// (big-endian bitstream, as ATRAC3 uses). Reads from a byte buffer; reads past the end
/// return zero bits so a truncated final frame still decodes deterministically.
/// </summary>
internal sealed class Atrac3BitReader {
  private readonly byte[] _data;
  private readonly int _offset;
  private readonly int _lengthBits;
  private int _bitPos;

  public Atrac3BitReader(byte[] data, int offset, int lengthBytes) {
    this._data = data;
    this._offset = offset;
    this._lengthBits = lengthBytes * 8;
    this._bitPos = 0;
  }

  /// <summary>Reads <paramref name="count"/> bits (0–32) MSB first; past EOF yields zeros.</summary>
  public int GetBits(int count) {
    var value = 0;
    for (var i = 0; i < count; ++i) {
      var bitIndex = this._bitPos;
      var bit = 0;
      if (bitIndex < this._lengthBits) {
        var byteIndex = this._offset + (bitIndex >> 3);
        if (byteIndex < this._data.Length)
          bit = (this._data[byteIndex] >> (7 - (bitIndex & 7))) & 1;
      }
      value = (value << 1) | bit;
      ++this._bitPos;
    }
    return value;
  }

  /// <summary>Reads a single bit.</summary>
  public int GetBit() => this.GetBits(1);

  /// <summary>Reads <paramref name="count"/> bits as a sign-extended (two's complement) value.</summary>
  public int GetSignedBits(int count) {
    if (count == 0)
      return 0;
    var v = this.GetBits(count);
    var signBit = 1 << (count - 1);
    return (v ^ signBit) - signBit;
  }
}
