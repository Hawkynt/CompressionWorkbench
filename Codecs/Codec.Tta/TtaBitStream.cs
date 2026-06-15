#pragma warning disable CS1591

namespace Codec.Tta;

/// <summary>
/// Least-significant-bit-first bit writer matching the TTA reference encoder's
/// cache: bits accumulate from bit 0 upward and complete bytes spill to the
/// output little-end first. Frames are flushed on a byte boundary.
/// </summary>
internal sealed class TtaBitWriter {
  private readonly List<byte> _bytes = [];
  private uint _cache;
  private int _bits;

  /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/> (0–32), LSB first.</summary>
  public void PutBits(uint value, int count) {
    while (count > 0) {
      var take = Math.Min(8 - this._bits, count);
      var mask = take == 32 ? 0xFFFFFFFFu : (1u << take) - 1;
      this._cache |= (value & mask) << this._bits;
      this._bits += take;
      value >>= take;
      count -= take;
      while (this._bits >= 8) {
        this._bytes.Add((byte)(this._cache & 0xFF));
        this._cache >>= 8;
        this._bits -= 8;
      }
    }
  }

  /// <summary>Writes <paramref name="count"/> zero bits followed by a single one bit (unary escape).</summary>
  public void PutUnary(int count) {
    while (count >= 1) {
      var run = Math.Min(count, 24);
      this.PutBits(0u, run);
      count -= run;
    }
    this.PutBits(1u, 1);
  }

  /// <summary>Flushes any partial byte (zero-padded) and returns the frame bytes.</summary>
  public byte[] Flush() {
    if (this._bits > 0) {
      this._bytes.Add((byte)(this._cache & 0xFF));
      this._cache = 0;
      this._bits = 0;
    }
    return this._bytes.ToArray();
  }
}

/// <summary>
/// Least-significant-bit-first bit reader, the inverse of <see cref="TtaBitWriter"/>.
/// </summary>
internal sealed class TtaBitReader {
  private readonly byte[] _data;
  private int _pos;
  private uint _cache;
  private int _bits;

  public TtaBitReader(byte[] data, int offset) {
    this._data = data;
    this._pos = offset;
  }

  /// <summary>Reads <paramref name="count"/> bits (0–32) LSB first.</summary>
  public uint GetBits(int count) {
    var result = 0u;
    var shift = 0;
    while (count > 0) {
      if (this._bits == 0) {
        if (this._pos >= this._data.Length)
          throw new InvalidDataException("Unexpected end of TTA frame data.");
        this._cache = this._data[this._pos++];
        this._bits = 8;
      }
      var take = Math.Min(this._bits, count);
      var mask = take == 32 ? 0xFFFFFFFFu : (1u << take) - 1;
      result |= (this._cache & mask) << shift;
      this._cache >>= take;
      this._bits -= take;
      shift += take;
      count -= take;
    }
    return result;
  }

  /// <summary>Counts zero bits up to the terminating one bit (unary value).</summary>
  public int GetUnary() {
    var count = 0;
    while (this.GetBits(1) == 0)
      ++count;
    return count;
  }
}
