using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.BitIO;

/// <summary>
///   Buffered bit reader with lookahead (peek/drop) capability.
///   Uses a ulong accumulator for fast multi-bit operations with up to 56 bits of lookahead.
///   Generic on <typeparamref name="TOrder" /> so the JIT monomorphizes
///   each bit-order path — zero branch overhead in hot loops.
/// </summary>
public sealed class BitBuffer<TOrder>
  where TOrder : struct, IBitOrder {
  private readonly Stream _stream;
  private ulong _buffer;

  /// <summary>
  ///   Initializes a new <see cref="BitBuffer{TOrder}" /> over the specified stream.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  public BitBuffer(Stream stream) => this._stream = stream ?? throw new ArgumentNullException(nameof(stream));

  /// <summary>
  ///   Gets the number of bits currently available in the buffer.
  /// </summary>
  public int BitsAvailable { get; private set; }

  /// <summary>
  ///   Ensures at least <paramref name="count" /> bits are available in the buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool EnsureBits(int count) {
    while (this.BitsAvailable < count) {
      var readByte = this._stream.ReadByte();
      if (readByte < 0)
        return false;

      this._buffer = TOrder.InsertByte(this._buffer, this.BitsAvailable, readByte);
      this.BitsAvailable += 8;
    }

    return true;
  }

  /// <summary>
  ///   Peeks at <paramref name="count" /> bits without consuming them.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint PeekBits(int count) {
    if (!this.EnsureBits(count))
      ThrowNotEnoughBits();

    return TOrder.Peek(this._buffer, this.BitsAvailable, count);
  }

  /// <summary>
  ///   Drops (consumes) <paramref name="count" /> bits from the buffer.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void DropBits(int count) {
    if (count > this.BitsAvailable)
      ThrowTooManyBitsDrop();

    this._buffer = TOrder.Drop(this._buffer, this.BitsAvailable, count);
    this.BitsAvailable -= count;
  }

  /// <summary>
  ///   Reads <paramref name="count" /> bits from the buffer, consuming them.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint ReadBits(int count) {
    var value = this.PeekBits(count);
    this.DropBits(count);
    return value;
  }

  /// <summary>
  ///   Aligns to the next byte boundary by dropping remaining bits in the current byte.
  /// </summary>
  public void AlignToByte() {
    var bitsToSkip = this.BitsAvailable % 8;
    if (bitsToSkip > 0)
      this.DropBits(bitsToSkip);
  }

  [DoesNotReturn]
  [StackTraceHidden]
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowNotEnoughBits() =>
    throw new EndOfStreamException("Not enough bits available for peek.");

  [DoesNotReturn]
  [StackTraceHidden]
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowTooManyBitsDrop() =>
    throw new InvalidOperationException("Cannot drop more bits than are available.");
}

/// <summary>
///   Non-generic <see cref="BitBuffer" /> for callers that select bit order at runtime.
///   Delegates to a <see cref="BitBuffer{TOrder}" /> internally.
/// </summary>
public sealed class BitBuffer {
  private readonly BitOrder _bitOrder;
  private readonly BitBuffer<LsbBitOrder>? _lsb;
  private readonly BitBuffer<MsbBitOrder>? _msb;

  /// <summary>
  ///   Initializes a new <see cref="BitBuffer" /> over the specified stream.
  /// </summary>
  public BitBuffer(Stream stream, BitOrder bitOrder = BitOrder.LsbFirst) {
    this._bitOrder = bitOrder;
    if (bitOrder == BitOrder.LsbFirst)
      this._lsb = new(stream);
    else
      this._msb = new(stream);
  }

  /// <summary>Gets the number of bits currently available.</summary>
  public int BitsAvailable => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.BitsAvailable
    : this._msb!.BitsAvailable;

  /// <summary>Ensures at least <paramref name="count" /> bits are available.</summary>
  public bool EnsureBits(int count) => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.EnsureBits(count)
    : this._msb!.EnsureBits(count);

  /// <summary>Peeks at <paramref name="count" /> bits without consuming.</summary>
  public uint PeekBits(int count) => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.PeekBits(count)
    : this._msb!.PeekBits(count);

  /// <summary>Drops <paramref name="count" /> bits.</summary>
  public void DropBits(int count) {
    if (this._bitOrder == BitOrder.LsbFirst)
      this._lsb!.DropBits(count);
    else
      this._msb!.DropBits(count);
  }

  /// <summary>Reads <paramref name="count" /> bits, consuming them.</summary>
  public uint ReadBits(int count) => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.ReadBits(count)
    : this._msb!.ReadBits(count);

  /// <summary>Aligns to the next byte boundary.</summary>
  public void AlignToByte() {
    if (this._bitOrder == BitOrder.LsbFirst)
      this._lsb!.AlignToByte();
    else
      this._msb!.AlignToByte();
  }

}
