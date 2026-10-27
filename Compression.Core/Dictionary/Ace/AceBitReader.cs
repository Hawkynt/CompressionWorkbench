namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// Bit reader for ACE compressed data.
/// Reads uint32 LE words, extracts bits MSB-first from a 32-bit buffer.
/// </summary>
internal sealed class AceBitReader {
  private readonly byte[] _data;
  private int _pos;
  private uint _buffer;
  private int _bitsAvailable;

  public AceBitReader(byte[] data) {
    this._data = data;
    this._pos = 0;
    this._buffer = 0;
    this._bitsAvailable = 0;
  }

  public int BitsAvailable => this._bitsAvailable + (this._data.Length - this._pos) * 8;

  public uint PeekBits(int count) {
    EnsureBits(count);
    return this._buffer >> (32 - count);
  }

  public void DropBits(int count) {
    this._buffer <<= count;
    this._bitsAvailable -= count;
  }

  public uint ReadBits(int count) {
    var value = PeekBits(count);
    DropBits(count);
    return value;
  }

  public uint ReadBit() => ReadBits(1);

  private void EnsureBits(int count) {
    while (this._bitsAvailable < count && this._pos + 1 < this._data.Length) {
      // Read 16 bits (LE word) and shift into buffer
      var word = (uint)(this._data[this._pos] | (this._data[this._pos + 1] << 8));
      this._pos += 2;
      this._buffer |= word << (16 - this._bitsAvailable);
      this._bitsAvailable += 16;
    }
    // Handle final byte if odd
    if (this._bitsAvailable < count && this._pos < this._data.Length) {
      uint b = this._data[this._pos++];
      this._buffer |= b << (24 - this._bitsAvailable);
      this._bitsAvailable += 8;
    }
  }
}
