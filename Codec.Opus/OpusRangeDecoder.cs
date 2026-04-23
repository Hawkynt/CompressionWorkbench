#pragma warning disable CS1591

namespace Codec.Opus;

/// <summary>
/// Opus range (entropy) decoder — the shared arithmetic-coder state used by
/// both the CELT and SILK bitstreams, per RFC 6716 §4.1 (entity
/// "<c>ec_dec</c>"). This is a clean-room port of libopus's
/// <c>entdec.c</c>/<c>entcode.c</c>.
/// <para>
/// The range coder reads forward from the start of the packet and also reads
/// raw/unstructured bits backward from the end of the packet. <see cref="ReadBitsRaw"/>
/// exposes the backward stream used for quantised pulse signs.
/// </para>
/// </summary>
public sealed class OpusRangeDecoder {

  private const int CodeBits = 32;
  private const uint CodeTop = 1u << 31;
  private const uint CodeBot = 1u << 23;
  private const int CodeExtra = 7;

  private readonly byte[] _buf;
  private readonly int _storage;
  private uint _val;
  private uint _rng;
  private int _offs;    // forward read cursor
  private int _endOffs; // backward cursor from the end
  private uint _endWindow;
  private int _nEndBits;
  private int _nBitsTotal;

  /// <summary>Creates a new range decoder over <paramref name="buffer"/>.</summary>
  public OpusRangeDecoder(ReadOnlySpan<byte> buffer) {
    this._buf = buffer.ToArray();
    this._storage = this._buf.Length;
    this._endOffs = 0;
    this._endWindow = 0;
    this._nEndBits = 0;
    this._offs = 0;
    this._rng = 128;
    this._val = (uint)(127 - (this.ReadByte() >> 1));
    this._nBitsTotal = CodeBits + 1;
    this.Normalize();
  }

  /// <summary>Total bits consumed so far (used for termination checks).</summary>
  public int Tell => this._nBitsTotal - BitsLeftInRange(this._rng);

  /// <summary>
  /// Decodes a symbol modelled by a cumulative-frequency table with total
  /// probability <paramref name="ft"/>. Returns the scaled frequency value
  /// (0 ≤ fs &lt; ft) which the caller must map through its CDF to find the symbol.
  /// </summary>
  public uint DecodeUniform(uint ft) {
    var rng = this._rng / ft;
    var fs = ft - Math.Min(this._val / rng + 1, ft);
    return fs;
  }

  /// <summary>
  /// Narrows the range after the symbol with cumulative frequency bounds
  /// <paramref name="fl"/>..<paramref name="fh"/> (out of <paramref name="ft"/>)
  /// was decoded.
  /// </summary>
  public void Update(uint fl, uint fh, uint ft) {
    var rng = this._rng / ft;
    this._val -= rng * (ft - fh);
    this._rng = fl > 0 ? rng * (fh - fl) : this._rng - rng * (ft - fh);
    this.Normalize();
  }

  /// <summary>
  /// Decodes <paramref name="bits"/> raw (uncompressed) bits from the backward
  /// stream — used by CELT's pulse-coding stage for sign and fine-energy bits.
  /// </summary>
  public uint ReadBitsRaw(int bits) {
    var window = this._endWindow;
    var available = this._nEndBits;
    if (available < bits) {
      do {
        window |= (uint)this.ReadByteFromEnd() << available;
        available += 8;
      } while (available <= CodeBits - 8);
    }
    var result = window & ((1u << bits) - 1);
    window >>= bits;
    available -= bits;
    this._endWindow = window;
    this._nEndBits = available;
    this._nBitsTotal += bits;
    return result;
  }

  private void Normalize() {
    while (this._rng <= CodeBot) {
      this._nBitsTotal += 8;
      this._rng <<= 8;
      var sym = ((uint)this.ReadByte() >> (8 - CodeExtra)) | ((this._val >> (CodeBits - 1 - CodeExtra)) & 0xFFu);
      this._val = ((this._val << 8) + (255u - sym)) & (CodeTop - 1);
    }
  }

  private byte ReadByte() {
    if (this._offs < this._storage) return this._buf[this._offs++];
    return 0;
  }

  private byte ReadByteFromEnd() {
    if (this._endOffs < this._storage) {
      var b = this._buf[this._storage - ++this._endOffs];
      return b;
    }
    return 0;
  }

  private static int BitsLeftInRange(uint rng) {
    var b = 0;
    while (rng > 1) { rng >>= 1; b++; }
    return b;
  }
}
