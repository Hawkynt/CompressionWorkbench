#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// MSB-first big-endian bit reader over a byte buffer, used to walk an AC-3 (ATSC A/52)
/// sync frame. Bits are consumed from the most significant bit of each byte downwards, which
/// matches the bit packing the A/52 specification uses throughout syncinfo / BSI / audblk.
/// </summary>
public sealed class Ac3BitReader {

  private readonly byte[] _data;
  private readonly int _endByte;
  private int _bytePos;
  private int _bitPos;

  /// <summary>
  /// Initializes a new instance of <see cref="Ac3BitReader"/>.
  /// </summary>
public Ac3BitReader(byte[] data, int offset, int length) {
    ArgumentNullException.ThrowIfNull(data);
    if (offset < 0 || length < 0 || offset + length > data.Length)
      throw new ArgumentOutOfRangeException(nameof(offset));
    this._data = data;
    this._bytePos = offset;
    this._endByte = offset + length;
  }

  /// <summary>Total bits remaining in the buffer.</summary>
  public long BitsRemaining => ((long)(this._endByte - this._bytePos) * 8) - this._bitPos;

  /// <summary>Reads <paramref name="count"/> bits (0..32) MSB-first, returning them right-aligned.</summary>
  public uint ReadBits(int count) {
    if (count is < 0 or > 32)
      throw new ArgumentOutOfRangeException(nameof(count));
    uint result = 0;
    for (var i = 0; i < count; ++i) {
      if (this._bytePos >= this._endByte)
        throw new InvalidDataException("Unexpected end of AC-3 bit stream.");
      var bit = (this._data[this._bytePos] >> (7 - this._bitPos)) & 1;
      result = (result << 1) | (uint)bit;
      if (++this._bitPos == 8) {
        this._bitPos = 0;
        ++this._bytePos;
      }
    }
    return result;
  }

  /// <summary>Reads a single bit as a bool.</summary>
  public bool ReadFlag() => this.ReadBits(1) != 0;

  /// <summary>Reads <paramref name="count"/> bits as a signed value (two's complement of width <paramref name="count"/>).</summary>
  public int ReadSigned(int count) {
    var raw = (int)this.ReadBits(count);
    var signBit = 1 << (count - 1);
    return (raw ^ signBit) - signBit;
  }

  /// <summary>Skips <paramref name="count"/> bits (may exceed 32).</summary>
  public void SkipBits(int count) {
    if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
    var total = this._bitPos + count;
    this._bytePos += total >> 3;
    this._bitPos = total & 7;
    if (this._bytePos > this._endByte)
      throw new InvalidDataException("Skip past end of AC-3 bit stream.");
  }
}
