using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Bit reader for Brotli streams. Reads bits LSB-first from a byte stream.
/// </summary>
internal sealed class BrotliBitReader {
  private readonly byte[] _data;
  private readonly int _dataLength;
  private int _bytePos;
  private ulong _bitBuffer;
  private int _bitsAvailable;

  /// <summary>
  /// Initializes a new <see cref="BrotliBitReader"/> over the given data.
  /// </summary>
  /// <param name="data">The compressed data.</param>
  public BrotliBitReader(byte[] data) : this(data, data.Length) { }

  /// <summary>
  /// Initializes a new <see cref="BrotliBitReader"/> over the given data with an explicit length.
  /// </summary>
  /// <param name="data">The buffer containing compressed data.</param>
  /// <param name="length">The number of valid bytes in the buffer.</param>
  public BrotliBitReader(byte[] data, int length) {
    this._data = data;
    this._dataLength = length;
    this._bytePos = 0;
    this._bitBuffer = 0;
    this._bitsAvailable = 0;
  }

  /// <summary>Gets the current byte position in the stream.</summary>
  public int BytePosition => this._bytePos - (this._bitsAvailable >> 3);

  /// <summary>Gets the exact bit position in the stream.</summary>
  public int BitPosition => this._bytePos * 8 - this._bitsAvailable;

  /// <summary>Debug: dump buffer state.</summary>
  public string DebugState => $"bytePos={this._bytePos} bitsAvail={this._bitsAvailable} buf=0x{this._bitBuffer:X16} bitPos={this.BitPosition}";

  /// <summary>Gets whether the reader has reached the end of the data.</summary>
  public bool IsAtEnd => this._bytePos >= this._dataLength && this._bitsAvailable == 0;

  /// <summary>
  /// Ensures at least <paramref name="count"/> bits are available in the buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Fill(int count) {
    while (this._bitsAvailable < count && this._bytePos < this._dataLength) {
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
    var consume = Math.Min(count, this._bitsAvailable);
    this._bitBuffer >>= consume;
    this._bitsAvailable -= consume;
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
    if (count <= 0) return;
    if (count > this._bitsAvailable) count = this._bitsAvailable;
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
