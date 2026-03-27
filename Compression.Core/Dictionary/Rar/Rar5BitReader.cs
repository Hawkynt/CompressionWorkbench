using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Bit reader for RAR5 compressed streams. Reads bits MSB-first (big-endian),
/// matching the bit ordering used by RAR and <see cref="Rar5BitWriter"/>.
/// </summary>
internal sealed class Rar5BitReader {
  private readonly byte[] _data;
  private int _bytePos;
  private uint _bitBuffer;
  private int _bitsAvailable;

  /// <summary>
  /// Initializes a new <see cref="Rar5BitReader"/> over the given data.
  /// </summary>
  /// <param name="data">The compressed data.</param>
  public Rar5BitReader(byte[] data) {
    this._data = data;
    this._bytePos = 0;
    this._bitBuffer = 0;
    this._bitsAvailable = 0;
  }

  /// <summary>Gets the current byte position in the stream.</summary>
  public int BytePosition => this._bytePos - (this._bitsAvailable >> 3);

  /// <summary>Gets whether the reader has reached the end of the data.</summary>
  public bool IsAtEnd => this._bytePos >= this._data.Length && this._bitsAvailable == 0;

  /// <summary>
  /// Ensures at least <paramref name="count"/> bits are available in the buffer.
  /// Bytes are loaded into the MSB end of the buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Fill(int count) {
    while (this._bitsAvailable < count && this._bytePos < this._data.Length) {
      this._bitBuffer |= (uint)this._data[this._bytePos++] << (24 - this._bitsAvailable);
      this._bitsAvailable += 8;
    }
  }

  /// <summary>
  /// Reads <paramref name="count"/> bits (MSB-first) and advances the position.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint ReadBits(int count) {
    this.Fill(count);
    var value = this._bitBuffer >> (32 - count);
    this._bitBuffer <<= count;
    this._bitsAvailable -= count;
    return value;
  }

  /// <summary>
  /// Peeks at the next <paramref name="count"/> bits (MSB-first) without advancing.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint PeekBits(int count) {
    this.Fill(count);
    return this._bitBuffer >> (32 - count);
  }

  /// <summary>
  /// Drops <paramref name="count"/> bits from the buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void DropBits(int count) {
    this._bitBuffer <<= count;
    this._bitsAvailable -= count;
  }

  /// <summary>
  /// Reads a single bit.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint ReadBit() => this.ReadBits(1);

  /// <summary>
  /// Aligns to the next byte boundary.
  /// </summary>
  public void AlignToByte() {
    var drop = this._bitsAvailable & 7;
    if (drop <= 0)
      return;

    this._bitBuffer <<= drop;
    this._bitsAvailable -= drop;
  }
}
