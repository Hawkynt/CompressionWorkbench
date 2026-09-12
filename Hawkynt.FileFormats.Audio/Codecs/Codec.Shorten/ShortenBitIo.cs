#pragma warning disable CS1591

namespace Codec.Shorten;

/// <summary>
/// MSB-first bit reader for the Shorten command stream. Implements the three Shorten
/// entropy primitives used throughout the format:
/// <list type="bullet">
///   <item><c>uvar_get(k)</c> — an unsigned Rice code: a unary high part (count of
///     leading zero bits, terminated by a 1) shifted left by <c>k</c>, OR-ed with a
///     <c>k</c>-bit binary low part.</item>
///   <item><c>ulong_get()</c> — a self-describing unsigned long: first a parameter
///     <c>k = uvar_get(ULONGSIZE)</c>, then the value <c>uvar_get(k)</c>.</item>
///   <item><c>var_get(k)</c> — a signed Rice code: <c>uvar_get(k)</c> with the LSB
///     used as a sign-fold (zig-zag), matching shorten's <c>uvar_get</c>/<c>var_get</c>.</item>
/// </list>
/// Shorten packs bits MSB-first into a 32-bit accumulator that is refilled one byte at
/// a time, which is the layout reproduced here.
/// </summary>
internal sealed class ShortenBitReader {
  private readonly byte[] _data;
  private int _bytePos;
  private int _bitsLeft; // bits remaining in _bitBuffer
  private uint _bitBuffer;

  public ShortenBitReader(byte[] data, int startByte) {
    this._data = data;
    this._bytePos = startByte;
    this._bitsLeft = 0;
    this._bitBuffer = 0;
  }

  /// <summary>True when no further whole bits can be produced from the backing buffer.</summary>
  public bool AtEnd => this._bitsLeft == 0 && this._bytePos >= this._data.Length;

  /// <summary>Reads a single bit (MSB-first), throwing past the end of the buffer.</summary>
  public int ReadBit() {
    if (this._bitsLeft == 0) {
      if (this._bytePos >= this._data.Length)
        throw new InvalidDataException("Unexpected end of Shorten stream.");
      this._bitBuffer = this._data[this._bytePos++];
      this._bitsLeft = 8;
    }

    --this._bitsLeft;
    return (int)((this._bitBuffer >> this._bitsLeft) & 1u);
  }

  /// <summary>Reads <paramref name="count"/> bits MSB-first into an unsigned value.</summary>
  public uint ReadBits(int count) {
    var result = 0u;
    for (var i = 0; i < count; ++i)
      result = (result << 1) | (uint)this.ReadBit();
    return result;
  }

  /// <summary>Shorten <c>uvar_get</c>: unary high part (k-shifted) plus a k-bit low part.</summary>
  public uint UVarGet(int k) {
    var high = 0u;
    while (this.ReadBit() == 0)
      ++high;
    var low = k > 0 ? this.ReadBits(k) : 0u;
    return (high << k) | low;
  }

  /// <summary>Shorten <c>ulong_get</c>: a k drawn with <see cref="UVarGet"/>(ULONGSIZE), then the value.</summary>
  public uint ULongGet() {
    var k = (int)this.UVarGet(ShortenConstants.UlongSize);
    return this.UVarGet(k);
  }

  /// <summary>Shorten <c>var_get</c>: signed Rice via LSB sign-fold (zig-zag) of <see cref="UVarGet"/>.</summary>
  public int VarGet(int k) {
    var u = this.UVarGet(k);
    return (u & 1) != 0 ? ~(int)(u >> 1) : (int)(u >> 1);
  }
}

/// <summary>
/// MSB-first bit writer mirroring <see cref="ShortenBitReader"/>. Emits the same three
/// Shorten primitives so the encoder produces a byte-exact inverse of the decoder.
/// </summary>
internal sealed class ShortenBitWriter {
  private readonly Stream _output;
  private uint _bitBuffer;
  private int _bitsFilled;

  public ShortenBitWriter(Stream output) {
    this._output = output;
  }

  /// <summary>Writes a single bit (MSB-first).</summary>
  public void WriteBit(int bit) {
    this._bitBuffer = (this._bitBuffer << 1) | (uint)(bit & 1);
    if (++this._bitsFilled != 8)
      return;

    this._output.WriteByte((byte)this._bitBuffer);
    this._bitBuffer = 0;
    this._bitsFilled = 0;
  }

  /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/> MSB-first.</summary>
  public void WriteBits(uint value, int count) {
    for (var i = count - 1; i >= 0; --i)
      this.WriteBit((int)((value >> i) & 1u));
  }

  /// <summary>Inverse of <see cref="ShortenBitReader.UVarGet"/>.</summary>
  public void UVarPut(uint value, int k) {
    var high = value >> k;
    for (var i = 0u; i < high; ++i)
      this.WriteBit(0);
    this.WriteBit(1);
    if (k > 0)
      this.WriteBits(value & ((1u << k) - 1), k);
  }

  /// <summary>Inverse of <see cref="ShortenBitReader.ULongGet"/>.</summary>
  public void ULongPut(uint value) {
    var k = 0;
    while (value >> k != 0)
      ++k;
    // shorten chooses k as the bit length; mirror its ulong_put (uvar with ULONGSIZE).
    this.UVarPut((uint)k, ShortenConstants.UlongSize);
    this.UVarPut(value, k);
  }

  /// <summary>Inverse of <see cref="ShortenBitReader.VarGet"/> (LSB sign-fold).</summary>
  public void VarPut(int value, int k) {
    var folded = value < 0 ? (uint)(~value << 1) | 1u : (uint)value << 1;
    this.UVarPut(folded, k);
  }

  /// <summary>Pads the final partial byte with 1 bits and flushes, matching shorten's terminator padding.</summary>
  public void Flush() {
    while (this._bitsFilled != 0)
      this.WriteBit(1);
  }
}
