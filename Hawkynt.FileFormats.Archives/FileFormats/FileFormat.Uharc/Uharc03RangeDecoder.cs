namespace FileFormat.Uharc;

/// <summary>
/// The range decoder of UHARC 0.3's LZP and ALZ coders (docs/UHARC-ON-DISK.md, "UHARC 0.3",
/// Range decoder): a 32-bit code fed one byte at a time with the top bit of each byte carried
/// over into the next, renormalised while the range is at most 2²³; the top symbol of every
/// interval receives the rounding remainder.
/// </summary>
internal sealed class Uharc03RangeDecoder {

  private const uint Bottom = 0x800000;
  private const int MaxOverrun = 8;

  private readonly byte[] _source;
  private readonly int _end;
  private int _position;
  private int _overrun;
  private uint _code;
  private uint _range;
  private uint _carry;
  private uint _unit;
  private uint _total;

  public Uharc03RangeDecoder(byte[] source, int offset, int length) {
    this._source = source;
    this._position = offset;
    this._end = offset + length;
    this.NextByte(); // the encoder's first byte carries no information
    this._carry = this.NextByte();
    this._range = 0x80;
    this._code = this._carry >> 1;
  }

  private uint NextByte() {
    if (this._position < this._end)
      return this._source[this._position++];

    if (++this._overrun > MaxOverrun)
      throw new InvalidDataException("UHARC: packed stream ends before the decoder finished.");

    return 0;
  }

  /// <summary>Scales the range to <paramref name="total"/> slots and returns the slot the code falls in.</summary>
  public uint GetCount(uint total) {
    while (this._range <= Bottom) {
      var b = this.NextByte();
      this._code = (this._code << 8) | ((this._carry & 1) << 7) | (b >> 1);
      this._range <<= 8;
      this._carry = b;
    }

    total &= 0xFFFF;
    if (total == 0)
      throw new InvalidDataException("UHARC: empty frequency table.");

    this._unit = this._range / total;
    if (this._unit == 0)
      throw new InvalidDataException("UHARC: range coder underflow.");

    this._total = total;
    var count = this._code / this._unit;
    return count >= total ? total - 1 : count;
  }

  /// <summary>Narrows to <c>[low, low + size)</c> of the slots set by the last <see cref="GetCount"/>.</summary>
  public void Consume(uint low, uint size) {
    if (size == 0 || low + size > this._total)
      throw new InvalidDataException("UHARC: zero-width symbol.");

    var skipped = this._unit * low;
    this._code -= skipped;
    this._range = low + size < this._total ? this._unit * size : this._range - skipped;
  }

  /// <summary>Decodes one equiprobable value in <c>[0, total)</c>.</summary>
  public uint DecodeUniform(uint total) {
    var value = this.GetCount(total);
    this.Consume(value, 1);
    return value;
  }
}
