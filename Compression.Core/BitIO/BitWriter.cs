using System.Runtime.CompilerServices;

namespace Compression.Core.BitIO;

/// <summary>
/// Writes individual bits and multi-bit values to a stream.
/// Generic on <typeparamref name="TOrder"/> so the JIT monomorphizes
/// each bit-order path — zero branch overhead in hot loops.
/// </summary>
public sealed class BitWriter<TOrder> where TOrder : struct, IBitOrder {
  private readonly Stream _stream;
  private int _buffer;
  private int _bitsInBuffer;

  /// <summary>
  /// Initializes a new <see cref="BitWriter{TOrder}"/> over the specified stream.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  public BitWriter(Stream stream) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
  }

  /// <summary>
  /// Gets the number of bits currently buffered.
  /// </summary>
  public int BitsInBuffer => this._bitsInBuffer;

  /// <summary>
  /// Writes a single bit to the stream.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void WriteBit(int bit) {
    this._buffer = TOrder.PlaceBit(this._buffer, this._bitsInBuffer, bit);
    ++this._bitsInBuffer;

    if (this._bitsInBuffer == 8) {
      this._stream.WriteByte((byte)this._buffer);
      this._buffer = 0;
      this._bitsInBuffer = 0;
    }
  }

  /// <summary>
  /// Writes multiple bits to the stream.
  /// </summary>
  /// <param name="value">The value whose bits to write.</param>
  /// <param name="count">The number of bits to write (1–32).</param>
  public void WriteBits(uint value, int count) {
    ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);

    for (int i = 0; i < count; ++i)
      WriteBit((int)(value >> TOrder.WriteBitIndex(count, i)) & 1);
  }

  /// <summary>
  /// Flushes any remaining buffered bits to the stream, padding with zero bits if needed.
  /// </summary>
  public void FlushBits() {
    if (this._bitsInBuffer > 0) {
      this._stream.WriteByte((byte)this._buffer);
      this._buffer = 0;
      this._bitsInBuffer = 0;
    }
  }
}

/// <summary>
/// Non-generic <see cref="BitWriter"/> for callers that select bit order at runtime.
/// Delegates to a <see cref="BitWriter{TOrder}"/> internally.
/// </summary>
public sealed class BitWriter {
  private readonly BitWriter<LsbBitOrder>? _lsb;
  private readonly BitWriter<MsbBitOrder>? _msb;
  private readonly BitOrder _bitOrder;

  /// <summary>
  /// Initializes a new <see cref="BitWriter"/> over the specified stream.
  /// </summary>
  public BitWriter(Stream stream, BitOrder bitOrder = BitOrder.LsbFirst) {
    this._bitOrder = bitOrder;
    if (bitOrder == BitOrder.LsbFirst)
      this._lsb = new BitWriter<LsbBitOrder>(stream);
    else
      this._msb = new BitWriter<MsbBitOrder>(stream);
  }

  /// <summary>Gets the number of bits currently buffered.</summary>
  public int BitsInBuffer => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.BitsInBuffer : this._msb!.BitsInBuffer;

  /// <summary>Writes a single bit.</summary>
  public void WriteBit(int bit) {
    if (this._bitOrder == BitOrder.LsbFirst)
      this._lsb!.WriteBit(bit);
    else
      this._msb!.WriteBit(bit);
  }

  /// <summary>Writes multiple bits.</summary>
  public void WriteBits(uint value, int count) {
    if (this._bitOrder == BitOrder.LsbFirst)
      this._lsb!.WriteBits(value, count);
    else
      this._msb!.WriteBits(value, count);
  }

  /// <summary>Flushes any remaining buffered bits.</summary>
  public void FlushBits() {
    if (this._bitOrder == BitOrder.LsbFirst)
      this._lsb!.FlushBits();
    else
      this._msb!.FlushBits();
  }
}
