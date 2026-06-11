#pragma warning disable CS1591

namespace FileFormat.Shorten;

/// <summary>
/// Minimal MSB-first bit reader for the Shorten header, supporting the format's
/// <c>uvar</c> (Rice/Golomb-style) and <c>ulong</c> variable-length integer
/// codings. This is bitstream parsing only — it reads enough of the header to
/// report stream parameters and does not perform any audio DSP decode.
/// </summary>
/// <remarks>
/// A <c>uvar(k)</c> value is a unary-coded high part (a run of zero bits ended by
/// a one bit) followed by <c>k</c> low bits: <c>value = (high &lt;&lt; k)
/// | low</c>. A <c>ulong</c> value first reads a small <c>uvar(ULONGSIZE)</c> to
/// obtain the bit-width <c>k</c>, then reads a <c>uvar(k)</c> for the value.
/// </remarks>
internal sealed class ShortenBitReader {
  private readonly byte[] _data;
  private int _bytePos;
  private int _bitBuffer;
  private int _bitsLeft;

  private const int UlongSize = 2; // ULONGSIZE in the reference implementation.

  public ShortenBitReader(byte[] data, int startByte) {
    _data = data;
    _bytePos = startByte;
  }

  private int ReadBit() {
    if (_bitsLeft == 0) {
      if (_bytePos >= _data.Length)
        throw new EndOfStreamException("Shorten bitstream exhausted.");
      _bitBuffer = _data[_bytePos++];
      _bitsLeft = 8;
    }
    --_bitsLeft;
    return (_bitBuffer >> _bitsLeft) & 1;
  }

  private long ReadBits(int count) {
    long v = 0;
    for (var i = 0; i < count; ++i)
      v = (v << 1) | (uint)ReadBit();
    return v;
  }

  // uvar: unary high part terminated by a 1 bit, then k explicit low bits.
  public long ReadUvar(int k) {
    long high = 0;
    while (ReadBit() == 0) {
      ++high;
      if (high > 1_000_000) throw new InvalidDataException("Shorten unary run too long.");
    }
    var low = k > 0 ? ReadBits(k) : 0;
    return (high << k) | low;
  }

  // ulong: read the bit-width via a small uvar, then the value via uvar(width).
  public long ReadUlong() {
    var k = (int)ReadUvar(UlongSize);
    if (k < 0 || k > 40) throw new InvalidDataException("Shorten ulong width out of range.");
    return ReadUvar(k);
  }
}
