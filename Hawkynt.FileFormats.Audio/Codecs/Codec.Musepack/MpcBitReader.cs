#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Most-significant-bit-first bit reader matching FFmpeg's <c>get_bits</c>
/// semantics: bits are consumed from the top of each byte downward, and a
/// multi-bit read returns the bits in big-endian order. This is the reader the
/// Musepack SV8 audio-packet decoder runs against; the SV8 <em>container</em>
/// varints are byte-aligned and parsed separately (see <see cref="MpcContainer"/>).
/// </summary>
internal sealed class MpcBitReader {
  private readonly byte[] _data;
  private readonly int _baseBitPos;
  private readonly int _end;
  private int _bitPos;

  public MpcBitReader(byte[] data, int offset, int length) {
    this._data = data;
    this._baseBitPos = offset * 8;
    this._bitPos = offset * 8;
    this._end = (offset + length) * 8;
  }

  /// <summary>Number of bits consumed since construction.</summary>
  public int BitsConsumed => this._bitPos - this._baseBitPos;

  /// <summary>Bits still available before the end of the buffer (negative once overread).</summary>
  public int BitsLeft => this._end - this._bitPos;

  /// <summary>Discards <paramref name="count"/> bits.</summary>
  public void SkipBits(int count) => this._bitPos += count;

  /// <summary>Reads <paramref name="count"/> bits (0–32) MSB-first and returns them right-aligned.</summary>
  public int GetBits(int count) {
    var result = 0;
    for (var i = 0; i < count; ++i)
      result = (result << 1) | this.GetBit();
    return result;
  }

  /// <summary>Reads a single bit (0 or 1); reads past the end return 0 (FFmpeg pads with zero).</summary>
  public int GetBit() {
    if (this._bitPos >= this._end) {
      ++this._bitPos; // keep advancing so BitsLeft reflects the overread
      return 0;
    }

    var byteIndex = this._bitPos >> 3;
    var bitInByte = 7 - (this._bitPos & 7);
    ++this._bitPos;
    return (this._data[byteIndex] >> bitInByte) & 1;
  }
}
