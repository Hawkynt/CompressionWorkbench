#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// MSB-first bit reader used for AAC/ADTS parsing. Bits are consumed from the most
/// significant bit of each byte downwards, matching the order specified by
/// ISO/IEC 14496-3 (AAC) and ISO/IEC 13818-7 (ADTS).
/// </summary>
public sealed class AacBitReader {

  private readonly byte[] _data;
  private readonly int _endByte;
  private int _bytePos;
  private int _bitPos;

  public AacBitReader(byte[] data) : this(data, 0, data?.Length ?? 0) { }

  public AacBitReader(byte[] data, int offset, int length) {
    ArgumentNullException.ThrowIfNull(data);
    if (offset < 0 || length < 0 || offset + length > data.Length)
      throw new ArgumentOutOfRangeException(nameof(offset));
    this._data = data;
    this._bytePos = offset;
    this._endByte = offset + length;
    this._bitPos = 0;
  }

  /// <summary>
  /// Current byte position, rounded up if any bits have been consumed in the current byte.
  /// </summary>
  public int BytePosition => this._bitPos == 0 ? this._bytePos : this._bytePos + 1;

  /// <summary>Total bits remaining in the stream.</summary>
  public long BitsRemaining => ((long)(this._endByte - this._bytePos) * 8) - this._bitPos;

  /// <summary>Reads <paramref name="count"/> bits (1..32) MSB-first and returns them right-aligned.</summary>
  public uint ReadBits(int count) {
    if (count is < 0 or > 32)
      throw new ArgumentOutOfRangeException(nameof(count));
    uint result = 0;
    for (var i = 0; i < count; ++i) {
      if (this._bytePos >= this._endByte)
        throw new InvalidDataException("Unexpected end of AAC bit stream.");
      var bit = (this._data[this._bytePos] >> (7 - this._bitPos)) & 1;
      result = (result << 1) | (uint)bit;
      ++this._bitPos;
      if (this._bitPos == 8) {
        this._bitPos = 0;
        ++this._bytePos;
      }
    }
    return result;
  }

  /// <summary>Peeks <paramref name="count"/> bits (1..32) without advancing the cursor.</summary>
  public uint PeekBits(int count) {
    var byteSave = this._bytePos;
    var bitSave = this._bitPos;
    var value = this.ReadBits(count);
    this._bytePos = byteSave;
    this._bitPos = bitSave;
    return value;
  }

  /// <summary>Skips <paramref name="count"/> bits (may be larger than 32).</summary>
  public void SkipBits(int count) {
    if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
    var total = this._bitPos + count;
    this._bytePos += total / 8;
    this._bitPos = total % 8;
    if (this._bytePos > this._endByte)
      throw new InvalidDataException("Skip past end of AAC bit stream.");
  }

  /// <summary>Aligns to the next byte boundary, if not already aligned.</summary>
  public void ByteAlign() {
    if (this._bitPos == 0) return;
    this._bitPos = 0;
    ++this._bytePos;
  }
}
