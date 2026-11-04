namespace FileFormat.StuffItX;

/// <summary>
/// Shared bit and byte reader of StuffIt X. Bits are read least-significant first; reading the first
/// bit of a byte consumes that whole byte, so <see cref="Position"/> always points past the byte the
/// bits come from. A byte read, a skip or <see cref="Align"/> drops the unread rest of that byte.
/// Reading past the end yields zeros up to a small slack, then throws, so a truncated stream cannot
/// loop forever.
/// </summary>
internal sealed class StuffItXInput(byte[] data, int offset = 0, int length = -1) {
  private const int SlackBytes = 16;

  private readonly byte[] _data = data;
  private readonly int _end = length < 0 ? data.Length : offset + length;
  private int _pos = offset;
  private int _bits;
  private int _bitsLeft;

  /// <summary>Offset of the next whole byte.</summary>
  public int Position {
    get => this._pos;
    set {
      this._pos = value;
      this._bitsLeft = 0;
    }
  }

  /// <summary>Index one past the last input byte.</summary>
  public int End => this._end;

  /// <summary>The underlying buffer.</summary>
  public byte[] Data => this._data;

  private int ByteAt(int index) {
    if (index < this._end) return this._data[index];
    if (index < this._end + SlackBytes) return 0;
    throw new InvalidDataException("StuffIt X stream ended early.");
  }

  /// <summary>Next whole byte; drops any unread bits of the previous byte.</summary>
  public int ReadByte() {
    this._bitsLeft = 0;
    return this.ByteAt(this._pos++);
  }

  /// <summary>Next big-endian 32-bit word.</summary>
  public uint ReadUInt32BigEndian() {
    var v = 0u;
    for (var i = 0; i < 4; ++i) v = (v << 8) | (uint)this.ReadByte();
    return v;
  }

  /// <summary>Skips <paramref name="count"/> whole bytes.</summary>
  public void Skip(long count) {
    if (count < 0 || this._pos + count > this._end + (long)SlackBytes) throw new InvalidDataException("StuffIt X length runs past the end.");
    this.Position = (int)(this._pos + count);
  }

  /// <summary>Copies the next <paramref name="count"/> whole bytes.</summary>
  public byte[] ReadBytes(long count) {
    if (count < 0 || this._pos + count > this._end) throw new InvalidDataException("StuffIt X length runs past the end.");
    var result = this._data.AsSpan(this._pos, (int)count).ToArray();
    this.Position = this._pos + (int)count;
    return result;
  }

  /// <summary>Drops the unread bits of the current byte.</summary>
  public void Align() => this._bitsLeft = 0;

  /// <summary>Next bit.</summary>
  public int ReadBit() {
    if (this._bitsLeft == 0) {
      this._bits = this.ByteAt(this._pos++);
      this._bitsLeft = 8;
    }
    var bit = this._bits & 1;
    this._bits >>= 1;
    --this._bitsLeft;
    return bit;
  }

  /// <summary>Next <paramref name="count"/> bits; the first bit read is bit 0 of the value.</summary>
  public int ReadBits(int count) {
    var v = 0;
    for (var i = 0; i < count; ++i) v |= this.ReadBit() << i;
    return v;
  }

  /// <summary>
  /// The "P2" variable-length number: a unary count n of one-bits (plus one), then bits until n
  /// one-bits have been seen, each one-bit setting the value bit at its position; the result is
  /// that value minus one.
  /// </summary>
  public ulong ReadP2() {
    var n = 1;
    while (this.ReadBit() == 1) {
      if (++n >= 64) throw new InvalidDataException("StuffIt X number is too long.");
    }
    ulong value = 0, bit = 1;
    var guard = 0;
    while (n > 0) {
      if (this.ReadBit() == 1) {
        --n;
        value |= bit;
      }
      bit <<= 1;
      if (++guard > 64) throw new InvalidDataException("StuffIt X number is too long.");
    }
    return value - 1;
  }

  /// <summary>A P2 number that must fit an <see cref="int"/>.</summary>
  public int ReadP2Int() {
    var v = this.ReadP2();
    return v <= int.MaxValue ? (int)v : throw new InvalidDataException("StuffIt X number is out of range.");
  }
}
