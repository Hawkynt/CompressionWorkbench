#pragma warning disable CS1591

namespace Codec.Vorbis;

/// <summary>
/// LSB-first bit reader for Vorbis packet payloads. Within each byte, bit 0 is
/// the first bit read. Multi-bit fields are assembled with the least-significant
/// of the first byte in the least-significant position of the result.
/// </summary>
internal sealed class VorbisBitReader {
  private readonly byte[] _data;
  private int _bytePos;
  private int _bitPos;
  private bool _eof;

  public VorbisBitReader(byte[] data) {
    this._data = data;
  }

  public bool Eof => this._eof;
  public int BytePosition => this._bytePos;

  public uint ReadBits(int count) {
    if (count == 0) return 0;
    uint value = 0;
    var shift = 0;
    while (count > 0) {
      if (this._bytePos >= this._data.Length) { this._eof = true; return 0; }
      var available = 8 - this._bitPos;
      var take = count < available ? count : available;
      var bits = (uint)((this._data[this._bytePos] >> this._bitPos) & ((1 << take) - 1));
      value |= bits << shift;
      shift += take;
      count -= take;
      this._bitPos += take;
      if (this._bitPos >= 8) { this._bitPos = 0; this._bytePos++; }
    }
    return value;
  }

  public int ReadSigned(int count) {
    var raw = (int)this.ReadBits(count);
    // sign-extend from 'count' bits
    var sign = 1 << (count - 1);
    if ((raw & sign) != 0) raw |= -1 << count;
    return raw;
  }

  public float ReadFloat32() {
    var bits = this.ReadBits(32);
    var mantissa = bits & 0x1FFFFF;
    var sign = (bits & 0x80000000) != 0;
    var exponent = (int)((bits & 0x7FE00000) >> 21);
    var value = (double)mantissa * Math.Pow(2.0, exponent - 788);
    return (float)(sign ? -value : value);
  }
}
