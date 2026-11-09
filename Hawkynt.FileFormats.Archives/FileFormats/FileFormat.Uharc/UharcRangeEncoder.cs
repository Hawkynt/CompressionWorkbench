namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of <see cref="UharcRangeDecoder"/> (docs/UHARC-ON-DISK.md, "Range decoder"):
/// a 32-bit range with a carry-propagating 64-bit low, so the stream starts with a zero byte and
/// the decoder's five-byte priming lines up.
/// </summary>
internal sealed class UharcRangeEncoder {

  private const uint Top = 1u << 24;

  private readonly MemoryStream _output = new();
  private ulong _low;
  private uint _range = uint.MaxValue;
  private byte _cache;
  private long _cacheSize = 1;

  /// <summary>Codes the slots <c>[low, low + size)</c> of <paramref name="total"/>.</summary>
  public void Encode(uint low, uint size, uint total) {
    if (size == 0 || low + size > total)
      throw new ArgumentOutOfRangeException(nameof(size));

    this._range /= total;
    this._low += (ulong)low * this._range;
    this._range *= size;
    this.Normalize();
  }

  /// <summary>Codes one bit against a 12-bit probability of zero and adapts it like the decoder.</summary>
  public void EncodeBit(ref ushort probability, int shift, int bit) {
    var bound = this._range >> 12;
    var p = (uint)probability;
    if (bit == 0) {
      this._range = bound * p;
      probability = (ushort)(p + ((4096 - p) >> shift));
    } else {
      this._low += (ulong)bound * p;
      this._range = bound * (4096 - p);
      probability = (ushort)(p - (p >> shift));
    }

    this.Normalize();
  }

  private void Normalize() {
    while (this._range < Top) {
      this._range <<= 8;
      this.ShiftLow();
    }
  }

  private void ShiftLow() {
    if (this._low < 0xFF000000UL || this._low > 0xFFFFFFFFUL) {
      var carry = (byte)(this._low >> 32);
      var temp = this._cache;
      do {
        this._output.WriteByte((byte)(temp + carry));
        temp = 0xFF;
      } while (--this._cacheSize != 0);

      this._cache = (byte)(this._low >> 24);
    }

    ++this._cacheSize;
    this._low = (this._low & 0x00FFFFFFUL) << 8;
  }

  /// <summary>Flushes the coder and returns the packed stream.</summary>
  public byte[] Finish() {
    for (var i = 0; i < 5; ++i)
      this.ShiftLow();
    return this._output.ToArray();
  }
}
