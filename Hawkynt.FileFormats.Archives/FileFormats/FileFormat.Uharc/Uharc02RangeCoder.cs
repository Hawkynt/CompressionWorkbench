namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.2's bit-wise range decoder (docs/UHARC-ON-DISK.md, "UHARC 0.2", Main coder): a 16-bit
/// code read most significant bit first, a value in <c>[0, t)</c> being
/// <c>((code + 1)·t − 1) / range</c>, and a narrowing that doubles the range, shifting in a bit,
/// while it is at most 0x8000.
/// </summary>
internal sealed class Uharc02RangeDecoder {

  private readonly byte[] _source;
  private readonly int _end;
  private int _position;
  private int _bitMask;
  private byte _currentByte;
  private uint _code;
  private uint _range;
  private uint _total;

  public Uharc02RangeDecoder(byte[] source, int offset, int length) {
    this._source = source;
    this._position = offset;
    this._end = offset + length;
    for (var i = 0; i < 16; ++i)
      this._code = (this._code << 1) | (uint)this.Bit();
    this._range = 0x10000;
  }

  private int Bit() {
    if (this._bitMask == 0) {
      if (this._position >= this._end) {
        // A few bits past the end are normal while the coder drains.
        if (this._position++ > this._end + 8)
          throw new InvalidDataException("UHARC: packed stream ends before the decoder finished.");
        this._currentByte = 0;
      } else
        this._currentByte = this._source[this._position++];
      this._bitMask = 0x80;
    }

    var bit = (this._currentByte & this._bitMask) != 0 ? 1 : 0;
    this._bitMask >>= 1;
    return bit;
  }

  /// <summary>The value the code stands for among <paramref name="total"/> slots.</summary>
  public uint Count(uint total) {
    if (total == 0 || this._range == 0)
      throw new InvalidDataException("UHARC: empty frequency table.");
    this._total = total;
    return (uint)((((ulong)this._code + 1) * total - 1) / this._range);
  }

  /// <summary>Narrows to the slots <c>[low, low + size)</c> of the last <see cref="Count"/>.</summary>
  public void Consume(uint low, uint size) {
    var total = this._total;
    var from = (uint)((ulong)low * this._range / total);
    var to = (uint)((ulong)(low + size) * this._range / total);
    this._code -= from;
    var span = to - from;
    while (span <= 0x8000) {
      span <<= 1;
      this._code = (this._code << 1) + (uint)this.Bit();
    }

    this._range = span;
  }

  /// <summary>One equiprobable value in <c>[0, total)</c>.</summary>
  public uint Uniform(uint total) {
    var value = this.Count(total);
    if (value >= total)
      throw new InvalidDataException("UHARC: range coder left its interval.");
    this.Consume(value, 1);
    return value;
  }
}

/// <summary>
/// The encoder side of <see cref="Uharc02RangeDecoder"/>: a 16-bit low over the bits written so far,
/// a carry out of it added into those bits, and the low's top bit shifted out whenever the range
/// doubles.
/// </summary>
internal sealed class Uharc02RangeEncoder {

  private readonly List<byte> _bytes = [];
  private long _bits;
  private uint _low;
  private uint _range = 0x10000;

  /// <summary>The total of the frequency decision under way.</summary>
  public uint PendingTotal { get; set; }

  /// <summary>Codes the slots <c>[low, low + size)</c> of <paramref name="total"/>.</summary>
  public void Encode(uint low, uint size, uint total) {
    if (size == 0 || total == 0 || low + size > total)
      throw new ArgumentOutOfRangeException(nameof(size));

    var from = (uint)((ulong)low * this._range / total);
    var to = (uint)((ulong)(low + size) * this._range / total);
    if (to == from)
      throw new InvalidOperationException("UHARC: 0.2 symbol too narrow for the range.");
    this._low += from;
    if (this._low >= 0x10000) {
      this._low -= 0x10000;
      this.Carry();
    }

    var span = to - from;
    while (span <= 0x8000) {
      span <<= 1;
      this.Emit((int)(this._low >> 15) & 1);
      this._low = (this._low << 1) & 0xFFFF;
    }

    this._range = span;
  }

  private void Emit(int bit) {
    var index = (int)(this._bits >> 3);
    if (index == this._bytes.Count)
      this._bytes.Add(0);
    if (bit != 0)
      this._bytes[index] |= (byte)(0x80 >> (int)(this._bits & 7));
    ++this._bits;
  }

  /// <summary>Adds one to the bits written so far, as a binary number.</summary>
  private void Carry() {
    for (var i = this._bits - 1; i >= 0; --i) {
      var index = (int)(i >> 3);
      var mask = (byte)(0x80 >> (int)(i & 7));
      if ((this._bytes[index] & mask) == 0) {
        this._bytes[index] |= mask;
        return;
      }

      this._bytes[index] &= (byte)~mask;
    }

    throw new InvalidOperationException("UHARC: 0.2 range coder carried past its first bit.");
  }

  /// <summary>Flushes the coder: the low's sixteen bits, then the stream.</summary>
  public byte[] Finish() {
    for (var i = 15; i >= 0; --i)
      this.Emit((int)(this._low >> i) & 1);
    return [.. this._bytes];
  }
}

/// <summary>The decoding side of the 0.2 coder for the shared model (<see cref="IUharcCoder"/>).</summary>
internal readonly struct Uharc02Decoding(Uharc02RangeDecoder coder) : IUharcCoder {

  public static bool Encoding => false;

  public uint Count(uint total, uint target) => coder.Count(total);

  public void Consume(uint low, uint size) => coder.Consume(low, size);

  public uint Uniform(uint total, uint value) => coder.Uniform(total);

  public int Bit(ref ushort probability, int shift, int bit) => throw new NotSupportedException("UHARC 0.2 codes no adaptive bits.");

  public bool Scaled(uint probability, uint total, bool first) => throw new NotSupportedException("UHARC 0.2 codes no scaled decisions.");
}

/// <summary>The encoding side of the 0.2 coder for the shared model (<see cref="IUharcCoder"/>).</summary>
internal readonly struct Uharc02Encoding(Uharc02RangeEncoder coder) : IUharcCoder {

  public static bool Encoding => true;

  public uint Count(uint total, uint target) {
    if (target >= total)
      throw new InvalidOperationException("UHARC: encoder target outside its interval.");
    coder.PendingTotal = total;
    return target;
  }

  public void Consume(uint low, uint size) => coder.Encode(low, size, coder.PendingTotal);

  public uint Uniform(uint total, uint value) {
    coder.Encode(value, 1, total);
    return value;
  }

  public int Bit(ref ushort probability, int shift, int bit) => throw new NotSupportedException("UHARC 0.2 codes no adaptive bits.");

  public bool Scaled(uint probability, uint total, bool first) => throw new NotSupportedException("UHARC 0.2 codes no scaled decisions.");
}
