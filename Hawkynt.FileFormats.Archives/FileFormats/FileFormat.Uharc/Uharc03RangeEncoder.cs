namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of <see cref="Uharc03RangeDecoder"/> (docs/UHARC-ON-DISK.md, "UHARC 0.3",
/// Range decoder): a 31-bit low with a carry bit, renormalised while the range is at most 2²³ by
/// emitting the low's top byte (held back while it is 0xFF, so a later carry can still reach
/// it); the top symbol of every interval keeps the rounding remainder, and the stream starts with
/// a byte the decoder skips.
/// </summary>
internal sealed class Uharc03RangeEncoder {

  private const uint Top = 1u << 31;
  private const uint Bottom = 1u << 23;
  private const int ShiftBits = 23;

  private readonly MemoryStream _output = new();
  private uint _low;
  private uint _range = Top;
  private byte _buffer;
  private int _pending;

  /// <summary>The total of the frequency decision under way (see <see cref="Uharc03Encoding"/>).</summary>
  public uint PendingTotal { get; set; }

  private void Normalize() {
    while (this._range <= Bottom) {
      if (this._low < 0xFFu << ShiftBits) {
        this.Flush(0, 0xFF);
        this._buffer = (byte)(this._low >> ShiftBits);
      } else if ((this._low & Top) != 0) {
        this.Flush(1, 0x00);
        this._buffer = (byte)(this._low >> ShiftBits);
      } else
        ++this._pending;

      this._range <<= 8;
      this._low = (this._low << 8) & (Top - 1);
    }
  }

  /// <summary>Writes the held byte (plus <paramref name="carry"/>) and the bytes held behind it.</summary>
  private void Flush(int carry, byte follower) {
    this._output.WriteByte((byte)(this._buffer + carry));
    for (; this._pending > 0; --this._pending)
      this._output.WriteByte(follower);
  }

  /// <summary>Codes the slots <c>[low, low + size)</c> of <paramref name="total"/>.</summary>
  public void Encode(uint low, uint size, uint total) {
    total &= 0xFFFF;
    if (size == 0 || total == 0 || low + size > total)
      throw new ArgumentOutOfRangeException(nameof(size));

    this.Normalize();
    var unit = this._range / total;
    var skipped = unit * low;
    this._range = low + size < total ? unit * size : this._range - skipped;
    this._low += skipped;
  }

  /// <summary>Flushes the coder and returns the packed stream.</summary>
  public byte[] Finish() {
    this.Normalize();
    var last = (this._low >> ShiftBits) + 1;
    if (last > 0xFF)
      this.Flush(1, 0x00);
    else
      this.Flush(0, 0xFF);
    this._output.WriteByte((byte)last);
    for (var i = 0; i < 4; ++i)
      this._output.WriteByte(0);
    return this._output.ToArray();
  }
}

/// <summary>The decoding side of the 0.3 coder for the shared models (<see cref="IUharcCoder"/>).</summary>
internal readonly struct Uharc03Decoding(Uharc03RangeDecoder coder) : IUharcCoder {

  public static bool Encoding => false;

  public uint Count(uint total, uint target) => coder.GetCount(total);

  public void Consume(uint low, uint size) => coder.Consume(low, size);

  public uint Uniform(uint total, uint value) => coder.DecodeUniform(total);

  public int Bit(ref ushort probability, int shift, int bit) => throw new NotSupportedException("UHARC 0.3 codes no adaptive bits.");

  public bool Scaled(uint probability, uint total, bool first) => throw new NotSupportedException("UHARC 0.3 codes no scaled decisions.");
}

/// <summary>The encoding side of the 0.3 coder for the shared models (<see cref="IUharcCoder"/>).</summary>
internal readonly struct Uharc03Encoding(Uharc03RangeEncoder coder) : IUharcCoder {

  public static bool Encoding => true;

  public uint Count(uint total, uint target) {
    if (target >= (total & 0xFFFF))
      throw new InvalidOperationException("UHARC: encoder target outside its interval.");
    coder.PendingTotal = total;
    return target;
  }

  public void Consume(uint low, uint size) => coder.Encode(low, size, coder.PendingTotal);

  public uint Uniform(uint total, uint value) {
    coder.Encode(value, 1, total);
    return value;
  }

  public int Bit(ref ushort probability, int shift, int bit) => throw new NotSupportedException("UHARC 0.3 codes no adaptive bits.");

  public bool Scaled(uint probability, uint total, bool first) => throw new NotSupportedException("UHARC 0.3 codes no scaled decisions.");
}
