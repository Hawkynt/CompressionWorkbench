#pragma warning disable CS1591

namespace Codec.Wma;

/// <summary>
/// MSB-first bit reader mirroring FFmpeg's <c>GetBitContext</c> semantics used by the
/// WMA decoder: bits are consumed from the most-significant bit of each byte; reads that
/// run past the configured limit return zero-extended values (the reference reader is
/// backed by a padded buffer) while <see cref="BitsLeft"/> goes negative, which the
/// decode loop uses to detect truncation. A separate bit limit can be set below the
/// buffer length so a sub-region (a single bit-reservoir frame) is read in isolation.
/// </summary>
internal sealed class WmaBitReader {

  private readonly byte[] _data;
  private readonly int _baseByte;
  private int _index;     // absolute bit index from the start of the buffer
  private int _limitBits; // absolute bit limit (exclusive)

  public WmaBitReader(byte[] data, int byteOffset, int bitLength) {
    this._data = data;
    this._baseByte = byteOffset;
    this._index = byteOffset * 8;
    this._limitBits = byteOffset * 8 + bitLength;
  }

  /// <summary>Bits consumed since the (re)initialised origin.</summary>
  public int BitsCount => this._index - this._baseByte * 8;

  /// <summary>Signed count of bits remaining before the limit (negative once overread).</summary>
  public int BitsLeft => this._limitBits - this._index;

  /// <summary>Reads <paramref name="n"/> bits (0..32) MSB-first, zero-extending past the limit.</summary>
  public uint GetBits(int n) {
    if (n == 0) return 0;
    uint result = 0;
    for (var i = 0; i < n; ++i) {
      uint bit = 0;
      if (this._index < this._limitBits) {
        var bytePos = this._index >> 3;
        if (bytePos < this._data.Length)
          bit = (uint)((this._data[bytePos] >> (7 - (this._index & 7))) & 1);
      }
      result = (result << 1) | bit;
      ++this._index;
    }
    return result;
  }

  /// <summary>Reads a single bit.</summary>
  public uint GetBit() => this.GetBits(1);

  /// <summary>Peeks <paramref name="n"/> bits without advancing.</summary>
  public uint PeekBits(int n) {
    var save = this._index;
    var v = this.GetBits(n);
    this._index = save;
    return v;
  }

  /// <summary>Skips <paramref name="n"/> bits (n may exceed 32).</summary>
  public void SkipBits(int n) => this._index += n;

  /// <summary>Aligns the cursor up to the next byte boundary.</summary>
  public void AlignToByte() {
    var rem = this._index & 7;
    if (rem != 0) this._index += 8 - rem;
  }
}
