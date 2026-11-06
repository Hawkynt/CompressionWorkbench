namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// SQX bit reader: the stream is a sequence of 32-bit little-endian words whose bits are taken
/// from the most significant downwards. Reading past the end yields zero bits.
/// </summary>
internal sealed class SqxBitReader(byte[] data, int offset, int length) {
  private readonly byte[] _data = data;
  private readonly int _offset = offset;
  private readonly int _length = length;
  private long _word;      // index of the current 32-bit word
  private int _bits;       // bits of it already consumed
  private uint _window = Word(data, offset, length, 0);

  /// <summary>Bits consumed so far.</summary>
  public long Position => this._word * 32 + this._bits;

  /// <summary>Whether the reader has gone past the end of the stream.</summary>
  public bool Overrun => this._word * 4 > this._length;

  public uint Peek(int n) => n == 0 ? 0 : n >= 32 ? this._window : this._window >> (32 - n);

  public void Skip(int n) {
    this._bits += n;
    this._word += this._bits >> 5;
    this._bits &= 31;
    this._window = Word(this._data, this._offset, this._length, this._word) << this._bits;
    if (this._bits != 0)
      this._window |= Word(this._data, this._offset, this._length, this._word + 1) >> (32 - this._bits);
  }

  public uint Read(int n) {
    var value = this.Peek(n);
    this.Skip(n);
    return value;
  }

  private static uint Word(byte[] data, int offset, int length, long index) {
    var at = index * 4;
    if (at + 4 <= length)
      return (uint)(data[offset + at] | data[offset + at + 1] << 8 | data[offset + at + 2] << 16 | data[offset + at + 3] << 24);
    uint value = 0;
    for (var i = 0; i < 4 && at + i < length; ++i)
      value |= (uint)data[offset + at + i] << (8 * i);
    return value;
  }
}

/// <summary>SQX bit writer, the mirror of <see cref="SqxBitReader"/>: 32-bit little-endian words, filled from the top bit, the last one zero-padded.</summary>
internal sealed class SqxBitWriter(int capacity) {
  private readonly List<byte> _bytes = new(capacity);
  private uint _word;
  private int _count;

  public void Write(uint value, int n) {
    if (n == 0)
      return;
    if (n < 32)
      value &= (1u << n) - 1;
    while (n > 0) {
      var room = 32 - this._count;
      var take = Math.Min(n, room);
      var part = take == n ? value : value >> (n - take);
      if (take < 32)
        part &= (1u << take) - 1;
      this._word |= take == 32 ? part : part << (room - take);
      this._count += take;
      n -= take;
      if (this._count == 32)
        this.Emit();
    }
  }

  public byte[] ToArray() {
    if (this._count > 0)
      this.Emit();
    return [.. this._bytes];
  }

  private void Emit() {
    this._bytes.Add((byte)this._word);
    this._bytes.Add((byte)(this._word >> 8));
    this._bytes.Add((byte)(this._word >> 16));
    this._bytes.Add((byte)(this._word >> 24));
    this._word = 0;
    this._count = 0;
  }
}
