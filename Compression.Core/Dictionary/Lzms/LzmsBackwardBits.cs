namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// The Huffman half of a chunk, which runs backwards from its end: sixteen-bit
/// little-endian units taken from the tail towards the head, bits within a unit
/// most significant first.
/// </summary>
internal sealed class LzmsBackwardBitReader {
  private readonly ReadOnlyMemory<byte> _data;
  private int _position;
  private uint _buffer;
  private int _held;

  public LzmsBackwardBitReader(ReadOnlyMemory<byte> data) {
    this._data = data;
    this._position = data.Length;
  }

  public int Read(int count) {
    var value = 0;
    for (var i = 0; i < count; ++i) value = (value << 1) | this.ReadOne();
    return value;
  }

  public int ReadOne() {
    if (this._held == 0) {
      var span = this._data.Span;
      var unit = this._position >= 2 ? span[this._position - 2] | (span[this._position - 1] << 8) : 0;
      this._position -= 2;
      this._buffer = (uint)unit;
      this._held = 16;
    }

    --this._held;
    return (int)((this._buffer >> this._held) & 1);
  }
}

/// <summary>
/// Collects the bits of the backward half. The first unit written lands last in
/// the chunk, so the first bit written is the first the reader sees.
/// </summary>
internal sealed class LzmsBackwardBitWriter {
  private readonly List<int> _bits = [];

  public void Write(int value, int count) {
    for (var i = count - 1; i >= 0; --i) this._bits.Add((value >> i) & 1);
  }

  public IReadOnlyList<ushort> Units() {
    var bits = new List<int>(this._bits);
    while (bits.Count % 16 != 0) bits.Add(0);
    var units = new List<ushort>(bits.Count / 16);
    for (var i = 0; i < bits.Count; i += 16) {
      var unit = 0;
      for (var j = 0; j < 16; ++j) unit = (unit << 1) | bits[i + j];
      units.Add((ushort)unit);
    }
    return units;
  }
}
