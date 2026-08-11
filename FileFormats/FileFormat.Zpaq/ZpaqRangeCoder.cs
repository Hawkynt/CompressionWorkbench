namespace FileFormat.Zpaq;

/// <summary>
/// Shared constants and the safety argument of the ZPAQ binary range coder.
/// </summary>
/// <remarks>
/// <para>
/// The coder is a carry-propagating binary range coder. It carries a 32-bit
/// <c>range</c> and an arbitrarily long <c>low</c>, of which only the pending
/// tail is held in memory: one cached byte plus a count of deferred <c>0xFF</c>
/// bytes that a later carry may still turn into <c>0x00</c>. Splitting the range
/// by <c>bound = (range / 2^16) * p</c> assigns the lower subrange to a 1 bit and
/// the upper subrange to a 0 bit, so the more confident the model is that a 1
/// follows, the cheaper coding that 1 becomes.
/// </para>
/// <para><b>Invariant.</b> On entry to every bit,
/// <c>2^24 &lt;= range &lt;= 2^32 - 1</c> and <c>1 &lt;= p &lt;= 65535</c>. Both
/// subranges are then non-empty, with width at least 256:
/// </para>
/// <list type="bullet">
///   <item><description>
///     the 1 subrange has width <c>bound = (range / 2^16) * p &gt;= (2^24 / 2^16) * 1 = 256</c>;
///   </description></item>
///   <item><description>
///     the 0 subrange has width
///     <c>range - bound &gt;= range - (range / 2^16) * 65535
///        = range - (range / 2^16) * 2^16 + (range / 2^16)
///        &gt;= (range / 2^16) &gt;= 256</c>,
///     because <c>range - (range / 2^16) * 2^16</c> is the non-negative remainder
///     of the integer division.
///   </description></item>
/// </list>
/// <para>
/// Renormalisation multiplies <c>range</c> by 256 while it is below 2^24. Since it
/// is never zero it reaches 2^24 in at most two steps, and since each step starts
/// from a value below 2^24 the result stays below 2^32, so the invariant is
/// restored rather than merely approached. The interval can therefore never
/// collapse to zero width, and the decoder — which performs the identical integer
/// arithmetic — always finds its code word strictly inside exactly one subrange.
/// </para>
/// <para>
/// The bound also keeps <c>low</c> small enough to compute exactly:
/// <c>low + range &lt; 2^33</c> holds initially and is preserved by both coding
/// (which never increases the sum) and renormalisation (where
/// <c>(low mod 2^24) + range &lt; 2^25</c> before the common factor of 256), so
/// <c>low</c> never needs more than 33 bits and the carry out of bit 32 is always
/// a single bit.
/// </para>
/// <para>
/// Reference: Matt Mahoney, <i>The ZPAQ Open Standard Format for Highly Compressed
/// Data</i> (level 1/2 specification), section on arithmetic coding; and
/// <i>Data Compression Explained</i>, chapter 4, on binary arithmetic coding with
/// carry counting.
/// </para>
/// </remarks>
public static class ZpaqRangeCoder {

  /// <summary>Lower bound of the normalised range; the coder renormalises below this.</summary>
  public const uint RangeMinimum = 0x01000000u;

  /// <summary>Value at or above which the top byte of <c>low</c> may still be bumped by a carry.</summary>
  public const uint CarryEdge = 0xFF000000u;

  /// <summary>Number of fractional bits in a probability, so probabilities span 0..2^16.</summary>
  public const int ProbabilityBits = 16;

  /// <summary>Smallest probability of a 1 bit the coder accepts.</summary>
  public const int MinimumProbability = 1;

  /// <summary>Largest probability of a 1 bit the coder accepts.</summary>
  public const int MaximumProbability = 65535;

  /// <summary>Number of <c>low</c> bytes the encoder pushes out when the message ends.</summary>
  public const int FlushBytes = 5;

  /// <summary>
  /// Computes the split point between the 1 subrange and the 0 subrange.
  /// </summary>
  /// <param name="range">The current range, at least <see cref="RangeMinimum"/>.</param>
  /// <param name="probabilityOfOne">Probability of a 1 bit, clamped into 1..65535.</param>
  /// <returns>The width of the 1 subrange, at least 256 and at least 256 below <paramref name="range"/>.</returns>
  public static uint Split(uint range, int probabilityOfOne) {
    var p = (uint)Math.Clamp(probabilityOfOne, MinimumProbability, MaximumProbability);
    return (range >> ProbabilityBits) * p;
  }
}

/// <summary>
/// Carry-propagating binary range encoder. See <see cref="ZpaqRangeCoder"/> for the
/// invariant that keeps both subranges non-empty.
/// </summary>
public sealed class ZpaqRangeEncoder {

  private readonly Stream _output;
  private ulong _low;
  private uint _range = uint.MaxValue;
  private byte _cache;
  private int _pending = 1;

  /// <summary>
  /// Creates an encoder writing its bytes to the given stream.
  /// </summary>
  /// <param name="output">Destination for the coded bytes.</param>
  /// <exception cref="ArgumentNullException">Thrown if <paramref name="output"/> is null.</exception>
  public ZpaqRangeEncoder(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    this._output = output;
  }

  /// <summary>Gets the current range, for tests that assert the coder invariant.</summary>
  public uint Range => this._range;

  /// <summary>
  /// Codes one bit against the given probability.
  /// </summary>
  /// <param name="bit">The bit to code; zero or non-zero.</param>
  /// <param name="probabilityOfOne">Probability that the bit is 1, clamped into 1..65535.</param>
  public void EncodeBit(int bit, int probabilityOfOne) {
    var bound = ZpaqRangeCoder.Split(this._range, probabilityOfOne);
    if (bit != 0)
      this._range = bound;
    else {
      this._low += bound;
      this._range -= bound;
    }

    while (this._range < ZpaqRangeCoder.RangeMinimum) {
      this.ShiftLow();
      this._range <<= 8;
    }
  }

  /// <summary>
  /// Pushes out the remaining bytes of <c>low</c>, ending the coded stream.
  /// </summary>
  public void Flush() {
    for (var i = 0; i < ZpaqRangeCoder.FlushBytes; ++i)
      this.ShiftLow();
  }

  /// <summary>
  /// Emits the top byte of <c>low</c>, deferring it while a later carry could still
  /// increment it, and shifts <c>low</c> up by one byte.
  /// </summary>
  private void ShiftLow() {
    var carry = (uint)(this._low >> 32);
    var value = (uint)this._low;

    // While the top byte is 0xFF a carry would ripple through it, so it is only
    // counted, not emitted. Once a byte arrives that either cannot carry or has
    // just carried, the whole deferred run is resolved at once.
    if (value < ZpaqRangeCoder.CarryEdge || carry != 0) {
      var held = this._cache;
      do {
        this._output.WriteByte((byte)(held + carry));
        held = 0xFF;
        --this._pending;
      } while (this._pending != 0);
      this._cache = (byte)(value >> 24);
    }

    ++this._pending;
    this._low = (ulong)(value & 0x00FFFFFFu) << 8;
  }
}

/// <summary>
/// Carry-propagating binary range decoder, the exact mirror of
/// <see cref="ZpaqRangeEncoder"/>.
/// </summary>
public sealed class ZpaqRangeDecoder {

  private readonly byte[] _input;
  private int _position;
  private uint _range = uint.MaxValue;
  private uint _code;

  /// <summary>
  /// Creates a decoder reading from the given buffer, priming its code word from
  /// the first <see cref="ZpaqRangeCoder.FlushBytes"/> bytes.
  /// </summary>
  /// <param name="input">Buffer holding the coded bytes.</param>
  /// <param name="offset">Index of the first coded byte.</param>
  /// <exception cref="ArgumentNullException">Thrown if <paramref name="input"/> is null.</exception>
  public ZpaqRangeDecoder(byte[] input, int offset) {
    ArgumentNullException.ThrowIfNull(input);
    this._input = input;
    this._position = offset;

    // The encoder's first flushed byte is always the initial empty cache, so the
    // code word is the five leading bytes read big-endian into 32 bits.
    for (var i = 0; i < ZpaqRangeCoder.FlushBytes; ++i)
      this._code = (this._code << 8) | this.NextByte();
  }

  /// <summary>Gets the current range, for tests that assert the coder invariant.</summary>
  public uint Range => this._range;

  /// <summary>
  /// Decodes one bit against the given probability.
  /// </summary>
  /// <param name="probabilityOfOne">Probability that the bit is 1, clamped into 1..65535.</param>
  /// <returns>The decoded bit, 0 or 1.</returns>
  public int DecodeBit(int probabilityOfOne) {
    var bound = ZpaqRangeCoder.Split(this._range, probabilityOfOne);

    int bit;
    if (this._code < bound) {
      bit = 1;
      this._range = bound;
    } else {
      bit = 0;
      this._code -= bound;
      this._range -= bound;
    }

    while (this._range < ZpaqRangeCoder.RangeMinimum) {
      this._range <<= 8;
      this._code = (this._code << 8) | this.NextByte();
    }

    return bit;
  }

  /// <summary>
  /// Reads the next coded byte, or zero past the end of the buffer. Past the end
  /// the encoder's flush has already pinned every remaining bit, so the padding
  /// value only has to be the same on both sides.
  /// </summary>
  private uint NextByte() =>
    this._position < this._input.Length ? this._input[this._position++] : 0u;
}
