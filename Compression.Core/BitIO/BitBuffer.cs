using System.Runtime.CompilerServices;

namespace Compression.Core.BitIO;

/// <summary>
/// Buffered bit reader with lookahead (peek/drop) capability.
/// Uses a ulong accumulator for fast multi-bit operations with up to 56 bits of lookahead.
/// Generic on <typeparamref name="TOrder"/> so the JIT monomorphizes
/// each bit-order path — zero branch overhead in hot loops.
/// </summary>
public sealed class BitBuffer<TOrder> where TOrder : struct, IBitOrder {
  private readonly Stream _stream;
  private ulong _buffer;
  private int _bitsInBuffer;

  /// <summary>
  /// Initializes a new <see cref="BitBuffer{TOrder}"/> over the specified stream.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  public BitBuffer(Stream stream) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
  }

  /// <summary>
  /// Gets the number of bits currently available in the buffer.
  /// </summary>
  public int BitsAvailable => this._bitsInBuffer;

  /// <summary>
  /// Ensures at least <paramref name="count"/> bits are available in the buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool EnsureBits(int count) {
    while (this._bitsInBuffer < count) {
      int b = this._stream.ReadByte();
      if (b < 0)
        return false;
      this._buffer = TOrder.InsertByte(this._buffer, this._bitsInBuffer, b);
      this._bitsInBuffer += 8;
    }
    return true;
  }

  /// <summary>
  /// Peeks at <paramref name="count"/> bits without consuming them.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint PeekBits(int count) {
    if (!EnsureBits(count))
      throw new EndOfStreamException("Not enough bits available for peek.");
    return TOrder.Peek(this._buffer, this._bitsInBuffer, count);
  }

  /// <summary>
  /// Drops (consumes) <paramref name="count"/> bits from the buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void DropBits(int count) {
    if (count > this._bitsInBuffer)
      throw new InvalidOperationException("Cannot drop more bits than are available.");
    this._buffer = TOrder.Drop(this._buffer, this._bitsInBuffer, count);
    this._bitsInBuffer -= count;
  }

  /// <summary>
  /// Reads <paramref name="count"/> bits from the buffer, consuming them.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint ReadBits(int count) {
    uint value = PeekBits(count);
    DropBits(count);
    return value;
  }

  /// <summary>
  /// Aligns to the next byte boundary by dropping remaining bits in the current byte.
  /// </summary>
  public void AlignToByte() {
    int bitsToSkip = this._bitsInBuffer % 8;
    if (bitsToSkip > 0)
      DropBits(bitsToSkip);
  }
}

/// <summary>
/// Non-generic <see cref="BitBuffer"/> for callers that select bit order at runtime.
/// Delegates to a <see cref="BitBuffer{TOrder}"/> internally.
/// </summary>
public sealed class BitBuffer {
  private readonly BitBuffer<LsbBitOrder>? _lsb;
  private readonly BitBuffer<MsbBitOrder>? _msb;
  private readonly BitOrder _bitOrder;

  /// <summary>
  /// Initializes a new <see cref="BitBuffer"/> over the specified stream.
  /// </summary>
  public BitBuffer(Stream stream, BitOrder bitOrder = BitOrder.LsbFirst) {
    this._bitOrder = bitOrder;
    if (bitOrder == BitOrder.LsbFirst)
      this._lsb = new BitBuffer<LsbBitOrder>(stream);
    else
      this._msb = new BitBuffer<MsbBitOrder>(stream);
  }

  /// <summary>Gets the number of bits currently available.</summary>
  public int BitsAvailable => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.BitsAvailable : this._msb!.BitsAvailable;

  /// <summary>Ensures at least <paramref name="count"/> bits are available.</summary>
  public bool EnsureBits(int count) => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.EnsureBits(count) : this._msb!.EnsureBits(count);

  /// <summary>Peeks at <paramref name="count"/> bits without consuming.</summary>
  public uint PeekBits(int count) => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.PeekBits(count) : this._msb!.PeekBits(count);

  /// <summary>Drops <paramref name="count"/> bits.</summary>
  public void DropBits(int count) {
    if (this._bitOrder == BitOrder.LsbFirst)
      this._lsb!.DropBits(count);
    else
      this._msb!.DropBits(count);
  }

  /// <summary>Reads <paramref name="count"/> bits, consuming them.</summary>
  public uint ReadBits(int count) => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.ReadBits(count) : this._msb!.ReadBits(count);

  /// <summary>Aligns to the next byte boundary.</summary>
  public void AlignToByte() {
    if (this._bitOrder == BitOrder.LsbFirst)
      this._lsb!.AlignToByte();
    else
      this._msb!.AlignToByte();
  }
}
