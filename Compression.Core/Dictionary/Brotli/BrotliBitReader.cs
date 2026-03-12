using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Bit reader for Brotli streams. Reads bits LSB-first from a byte stream.
/// </summary>
internal sealed class BrotliBitReader {
  private readonly byte[] _data;
  private int _bytePos;
  private ulong _bitBuffer;
  private int _bitsAvailable;

  /// <summary>
  /// Initializes a new <see cref="BrotliBitReader"/> over the given data.
  /// </summary>
  /// <param name="data">The compressed data.</param>
  public BrotliBitReader(byte[] data) {
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
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Fill(int count) {
    while (this._bitsAvailable < count && this._bytePos < this._data.Length) {
      this._bitBuffer |= (ulong)this._data[this._bytePos++] << this._bitsAvailable;
      this._bitsAvailable += 8;
    }
  }

  /// <summary>
  /// Reads <paramref name="count"/> bits and advances the position.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint ReadBits(int count) {
    this.Fill(count);
    var value = (uint)(this._bitBuffer & ((1UL << count) - 1));
    this._bitBuffer >>= count;
    this._bitsAvailable -= count;
    return value;
  }

  /// <summary>
  /// Peeks at the next <paramref name="count"/> bits without advancing.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint PeekBits(int count) {
    this.Fill(count);
    return (uint)(this._bitBuffer & ((1UL << count) - 1));
  }

  /// <summary>
  /// Drops <paramref name="count"/> bits from the buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void DropBits(int count) {
    this._bitBuffer >>= count;
    this._bitsAvailable -= count;
  }

  /// <summary>
  /// Reads a single bit.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool ReadBool() => this.ReadBits(1) != 0;

  /// <summary>
  /// Aligns to the next byte boundary by dropping partial-byte bits.
  /// </summary>
  public void AlignToByte() {
    var drop = this._bitsAvailable & 7;
    if (drop <= 0)
      return;

    this._bitBuffer >>= drop;
    this._bitsAvailable -= drop;
  }
}
