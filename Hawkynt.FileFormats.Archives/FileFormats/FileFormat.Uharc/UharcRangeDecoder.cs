namespace FileFormat.Uharc;

/// <summary>
/// The 32-bit carry-less range decoder shared by every UHARC 0.6 entropy coder
/// (docs/UHARC-ON-DISK.md, "Range decoder").
/// </summary>
/// <remarks>
/// <c>code</c> is primed with five big-endian bytes and <c>range</c> starts at
/// <c>0xFFFFFFFF</c>; after every narrowing, bytes are shifted in while
/// <c>range &lt; 2^24</c>. Reads past the end of the packed stream yield zero bytes; a
/// handful are normal at the very end, more than <see cref="MaxOverrun"/> means the stream
/// is truncated.
/// </remarks>
internal sealed class UharcRangeDecoder {

  private const uint Top = 1u << 24;
  private const int MaxOverrun = 8;

  private readonly byte[] _source;
  private readonly int _end;
  private int _position;
  private int _overrun;
  private uint _code;
  private uint _range = uint.MaxValue;

  public UharcRangeDecoder(byte[] source, int offset, int length) {
    this._source = source;
    this._position = offset;
    this._end = offset + length;
    for (var i = 0; i < 5; ++i)
      this._code = (this._code << 8) | this.NextByte();
  }

  private uint NextByte() {
    if (this._position < this._end)
      return this._source[this._position++];

    if (++this._overrun > MaxOverrun)
      throw new InvalidDataException("UHARC: packed stream ends before the decoder finished.");

    return 0;
  }

  private void Normalize() {
    while (this._range < Top) {
      this._code = (this._code << 8) | this.NextByte();
      this._range <<= 8;
    }
  }

  /// <summary>Scales the range to <paramref name="total"/> slots and returns the slot the code falls in.</summary>
  public uint GetCount(uint total) {
    if (total == 0)
      throw new InvalidDataException("UHARC: empty frequency table.");

    this._range /= total;
    var count = this._code / this._range;
    if (count >= total)
      throw new InvalidDataException("UHARC: range coder left its interval.");

    return count;
  }

  /// <summary>Narrows to <c>[low, low + size)</c> of the slots set by the last <see cref="GetCount"/>.</summary>
  public void Consume(uint low, uint size) {
    if (size == 0)
      throw new InvalidDataException("UHARC: zero-width symbol.");

    this._code -= low * this._range;
    this._range *= size;
    this.Normalize();
  }

  /// <summary>Decodes one equiprobable value in <c>[0, total)</c>.</summary>
  public uint DecodeUniform(uint total) {
    var value = this.GetCount(total);
    this.Consume(value, 1);
    return value;
  }

  /// <summary>
  /// Decodes one bit against a 12-bit probability of zero and adapts it by <paramref name="shift"/>.
  /// </summary>
  public int DecodeBit(ref ushort probability, int shift) {
    this._range >>= 12;
    if (this._range == 0)
      throw new InvalidDataException("UHARC: range coder underflow.");

    var p = (uint)probability;
    var count = this._code / this._range;
    int bit;
    if (count < p) {
      this._range *= p;
      probability = (ushort)(p + ((4096 - p) >> shift));
      bit = 0;
    } else {
      if (count >= 4096)
        throw new InvalidDataException("UHARC: range coder left its interval.");

      this._code -= this._range * p;
      this._range *= 4096 - p;
      probability = (ushort)(p - (p >> shift));
      bit = 1;
    }

    this.Normalize();
    return bit;
  }
}
