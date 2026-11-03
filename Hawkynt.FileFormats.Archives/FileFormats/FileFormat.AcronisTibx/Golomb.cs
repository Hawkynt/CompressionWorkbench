#pragma warning disable CS1591
namespace FileFormat.AcronisTibx;

/// <summary>
///   Rice/Golomb codec with Rice parameter <c>k = 8</c> (divisor <c>m = 256</c>) — the
///   primitive Acronis <c>.tibx</c> uses to encode the GOLOMB-page probabilistic membership
///   filter.
/// </summary>
/// <remarks>
///   <para>
///     <b>Code layout.</b>
///     <list type="bullet">
///       <item><description>Decoding reads a unary-coded quotient (count of 1-bits ended by a
///         0-bit), caps the quotient at 8 (an escape path for larger values), then reads an
///         8-bit remainder. The final value is <c>(quotient &lt;&lt; 8) | remainder</c>.</description></item>
///       <item><description>Encoding is symmetric — <c>quotient</c> 1-bits, a terminating
///         0-bit, then an 8-bit remainder.</description></item>
///       <item><description>The GOLOMB-page sub-header carries <c>offset</c>, <c>lvl</c>,
///         <c>hash_shift</c>, <c>size</c>, <c>golomb_limit</c>, <c>nr_items</c> and
///         <c>nr_exts</c> — those parameters tune the underlying hash distribution but the
///         per-value bit shape is always <c>unary(q) || 0 || 8-bit(r)</c>.</description></item>
///     </list>
///   </para>
///   <para>
///     <b>What this codec is used for.</b> Acronis stores a per-ctree probabilistic membership
///     filter as a <c>Golomb-Coded Set (GCS)</c> on GOLOMB pages — item ids are hashed, sorted,
///     deltas computed, and each delta is Rice-coded with <c>k=8</c>. The codec is NOT directly
///     used to compress the LSM_LEAF record stream (that body is LZ4-stream-compressed; see
///     <see cref="AcronisTibxLsmRecord"/>). It is, however, the building block referenced by the
///     blocker note in this format's metadata and is documented + implemented here so a future
///     pass can decode the GCS to enumerate which item-ids a ctree contains.
///   </para>
///   <para>
///     <b>Bit order.</b> The bit stream is packed into <b>big-endian</b> 32-bit words and
///     bits are consumed MSB-first; the encoder symmetrically places each new bit into the
///     MSB.
///   </para>
///   <para>
///     <b>Escape path for quotient ≥ 8.</b> When the unary quotient reaches 8, the decoder
///     falls through to a 64-bit raw-value read that consumes the next 64 bits of the bit
///     stream verbatim. This caps
///     the per-value bit cost: small deltas use the cheap Rice form, but the rare large delta
///     pays the 64-bit literal. The decoder here mirrors that escape so a forged stream with
///     a huge gap still parses.
///   </para>
/// </remarks>
public static class Golomb {

  /// <summary>Rice parameter <c>k</c> (the remainder field width in bits). Always 8 for
  /// Acronis <c>.tibx</c>.</summary>
  public const int RiceK = 8;

  /// <summary>Divisor <c>m = 1 &lt;&lt; RiceK = 256</c>.</summary>
  public const int Divisor = 1 << RiceK;

  /// <summary>Unary-quotient cap before the 64-bit escape path kicks in.</summary>
  public const int QuotientEscape = 8;

  /// <summary>
  ///   Reader for an MSB-first bit stream packed byte-by-byte — the externally observable
  ///   bit ordering is <i>MSB-of-byte-first</i>, which is what this class produces.
  /// </summary>
  public sealed class BitReader {
    private readonly byte[] _data;
    private int _byteOffset;
    private ulong _bits;
    private int _bitsAvailable;

    /// <summary>Bytes consumed from the underlying byte stream so far.</summary>
    public int BytesConsumed => this._byteOffset;

    /// <summary>Number of buffered bits ready to be read without touching the byte stream.</summary>
    public int BufferedBitCount => this._bitsAvailable;

    /// <summary>
    ///   Wraps a byte buffer as an MSB-first BE bit stream.
    /// </summary>
    /// <param name="data">Backing buffer.</param>
    public BitReader(byte[] data) {
      ArgumentNullException.ThrowIfNull(data);
      this._data = data;
    }

    /// <summary>
    ///   Reads the next <paramref name="count"/> bits (0..56) MSB-first and returns them
    ///   right-aligned in the low bits of the returned value. When the underlying stream
    ///   is exhausted the missing bits are returned as zeros — a soft zero so callers can
    ///   probe truncated buffers.
    /// </summary>
    public ulong ReadBits(int count) {
      if (count is < 0 or > 56)
        throw new ArgumentOutOfRangeException(nameof(count),
          "Bit count must be in 0..56 (refill needs 8 bits of headroom).");
      if (count == 0) return 0;
      while (this._bitsAvailable < count && this._byteOffset < this._data.Length) {
        var b = this._data[this._byteOffset++];
        this._bits = (this._bits << 8) | b;
        this._bitsAvailable += 8;
      }
      if (this._bitsAvailable < count) {
        // Drain whatever bits are left, padded with zero on the LSB side.
        var pad = count - this._bitsAvailable;
        var partial = this._bits << pad;
        this._bits = 0;
        this._bitsAvailable = 0;
        return partial & MaskFor(count);
      }
      var shift = this._bitsAvailable - count;
      var v = (this._bits >> shift) & MaskFor(count);
      this._bits &= (1UL << shift) - 1UL;
      this._bitsAvailable -= count;
      return v;
    }

    /// <summary>Reads a 64-bit value as two 32-bit halves (MSB-first).</summary>
    public ulong ReadBits64() {
      var hi = this.ReadBits(32);
      var lo = this.ReadBits(32);
      return (hi << 32) | lo;
    }

    /// <summary>Reads one MSB-first bit, returning <c>0</c> or <c>1</c>.</summary>
    public int ReadBit() => (int)this.ReadBits(1);

    private static ulong MaskFor(int count) =>
      count >= 64 ? ulong.MaxValue : (1UL << count) - 1UL;
  }

  /// <summary>
  ///   Writer for the same MSB-first bit stream the reader consumes. Bytes are flushed as
  ///   soon as 8 bits accumulate; the final <see cref="Flush"/> zero-pads any partial byte.
  /// </summary>
  public sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private ulong _bits;
    private int _bitsBuffered;

    /// <summary>Bytes emitted so far (not counting partially-buffered bits).</summary>
    public int BytesWritten => this._bytes.Count;

    /// <summary>Currently-buffered (not-yet-flushed) bit count.</summary>
    public int BufferedBitCount => this._bitsBuffered;

    /// <summary>
    ///   Writes the low <paramref name="count"/> bits of <paramref name="value"/> MSB-first.
    /// </summary>
    public void WriteBits(ulong value, int count) {
      if (count is < 0 or > 56)
        throw new ArgumentOutOfRangeException(nameof(count),
          "Bit count must be in 0..56 (refill needs 8 bits of headroom).");
      if (count == 0) return;
      this._bits = (this._bits << count) | (value & MaskFor(count));
      this._bitsBuffered += count;
      while (this._bitsBuffered >= 8) {
        this._bitsBuffered -= 8;
        var b = (byte)((this._bits >> this._bitsBuffered) & 0xFF);
        this._bytes.Add(b);
        this._bits &= (1UL << this._bitsBuffered) - 1UL;
      }
    }

    /// <summary>Writes a 64-bit value as two 32-bit halves (MSB-first).</summary>
    public void WriteBits64(ulong value) {
      this.WriteBits(value >> 32, 32);
      this.WriteBits(value & 0xFFFFFFFFUL, 32);
    }

    /// <summary>Writes one bit (<c>0</c> or <c>1</c>).</summary>
    public void WriteBit(int bit) => this.WriteBits((ulong)(bit & 1), 1);

    /// <summary>
    ///   Flushes any partial trailing byte (zero-padding the LSB side) and returns the
    ///   accumulated byte stream.
    /// </summary>
    public byte[] Flush() {
      if (this._bitsBuffered > 0) {
        var pad = 8 - this._bitsBuffered;
        var b = (byte)((this._bits << pad) & 0xFF);
        this._bytes.Add(b);
        this._bits = 0;
        this._bitsBuffered = 0;
      }
      return [.. this._bytes];
    }

    private static ulong MaskFor(int count) =>
      count >= 64 ? ulong.MaxValue : (1UL << count) - 1UL;
  }

  /// <summary>
  ///   Encodes a single non-negative integer with Rice parameter <c>k = 8</c>. Layout:
  ///   <c>quotient</c> 1-bits, a 0-bit terminator, then an 8-bit remainder.
  /// </summary>
  /// <remarks>
  ///   When <c>quotient &gt;= 8</c> the encoding takes an escape path that emits eight 1-bits
  ///   as a sentinel followed by the full 64-bit value (no separator), matching the decoder's
  ///   quotient cap and subsequent 64-bit raw read.
  /// </remarks>
  public static void EncodeMod256(BitWriter w, ulong value) {
    ArgumentNullException.ThrowIfNull(w);
    var quotient = value >> RiceK;
    var remainder = (byte)(value & 0xFF);
    if (quotient >= (ulong)QuotientEscape) {
      // Escape: 8 sentinel 1-bits, then the raw 64-bit value.
      for (var i = 0; i < QuotientEscape; i++) w.WriteBit(1);
      w.WriteBits64(value);
      return;
    }
    for (var i = 0; i < (int)quotient; i++) w.WriteBit(1);
    w.WriteBit(0);
    w.WriteBits(remainder, RiceK);
  }

  /// <summary>
  ///   Decodes a single value previously written by <see cref="EncodeMod256"/>.
  /// </summary>
  public static ulong DecodeMod256(BitReader r) {
    ArgumentNullException.ThrowIfNull(r);
    var quotient = 0;
    while (quotient < QuotientEscape && r.ReadBit() == 1)
      quotient++;
    if (quotient >= QuotientEscape) {
      // Escape: next 64 bits are the raw value.
      return r.ReadBits64();
    }
    var remainder = r.ReadBits(RiceK);
    return ((ulong)quotient << RiceK) | remainder;
  }

  /// <summary>
  ///   Round-trip helper — encodes a sequence of values and returns the packed byte stream.
  /// </summary>
  public static byte[] EncodeSequenceMod256(IEnumerable<ulong> values) {
    ArgumentNullException.ThrowIfNull(values);
    var w = new BitWriter();
    foreach (var v in values) EncodeMod256(w, v);
    return w.Flush();
  }

  /// <summary>
  ///   Round-trip helper — decodes <paramref name="count"/> values from a packed byte stream.
  /// </summary>
  public static ulong[] DecodeSequenceMod256(byte[] data, int count) {
    ArgumentNullException.ThrowIfNull(data);
    if (count < 0)
      throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");
    var r = new BitReader(data);
    var values = new ulong[count];
    for (var i = 0; i < count; i++) values[i] = DecodeMod256(r);
    return values;
  }
}
