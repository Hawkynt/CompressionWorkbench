using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.BitIO;

/// <summary>
/// Reads individual bits and multi-bit values from a stream.
/// Generic on <typeparamref name="TOrder"/> so the JIT monomorphizes
/// each bit-order path — zero branch overhead in hot loops.
/// </summary>
public sealed class BitReader<TOrder> where TOrder : struct, IBitOrder {
  private readonly Stream _stream;
  private int _buffer;
  private int _bitsInBuffer;

  /// <summary>
  /// Initializes a new <see cref="BitReader{TOrder}"/> over the specified stream.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  public BitReader(Stream stream) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
  }

  /// <summary>
  /// Gets the number of bits remaining in the current byte buffer.
  /// </summary>
  public int BitsInBuffer => this._bitsInBuffer;

  /// <summary>
  /// Reads a single bit from the stream.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int ReadBit() {
    if (this._bitsInBuffer == 0) {
      int b = this._stream.ReadByte();
      if (b < 0)
        ThrowEndOfStream();
      this._buffer = b;
      this._bitsInBuffer = 8;
    }

    var (bit, buf) = TOrder.ExtractBit(this._buffer);
    this._buffer = buf;
    --this._bitsInBuffer;
    return bit;
  }

  /// <summary>
  /// Reads multiple bits from the stream.
  /// </summary>
  /// <param name="count">The number of bits to read (1–32).</param>
  public uint ReadBits(int count) {
    ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);

    var result = 0U;
    for (int i = 0; i < count; ++i)
      result = TOrder.AccumulateBits(result, ReadBit(), i);
    return result;
  }

  /// <summary>
  /// Discards any remaining bits in the current byte, aligning to the next byte boundary.
  /// </summary>
  public void AlignToByte() {
    this._bitsInBuffer = 0;
    this._buffer = 0;
  }

  [DoesNotReturn]
  private static void ThrowEndOfStream() =>
    throw new EndOfStreamException("Unexpected end of stream while reading bits.");
}

/// <summary>
/// Non-generic <see cref="BitReader"/> for callers that select bit order at runtime.
/// Delegates to a <see cref="BitReader{TOrder}"/> internally.
/// </summary>
public sealed class BitReader {
  private readonly BitReader<LsbBitOrder>? _lsb;
  private readonly BitReader<MsbBitOrder>? _msb;
  private readonly BitOrder _bitOrder;

  /// <summary>
  /// Initializes a new <see cref="BitReader"/> over the specified stream.
  /// </summary>
  public BitReader(Stream stream, BitOrder bitOrder = BitOrder.LsbFirst) {
    this._bitOrder = bitOrder;
    if (bitOrder == BitOrder.LsbFirst)
      this._lsb = new BitReader<LsbBitOrder>(stream);
    else
      this._msb = new BitReader<MsbBitOrder>(stream);
  }

  /// <summary>Gets the number of bits remaining in the current byte buffer.</summary>
  public int BitsInBuffer => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.BitsInBuffer : this._msb!.BitsInBuffer;

  /// <summary>Reads a single bit from the stream.</summary>
  public int ReadBit() => this._bitOrder == BitOrder.LsbFirst
    ? this._lsb!.ReadBit() : this._msb!.ReadBit();

  /// <summary>Reads multiple bits from the stream.</summary>
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
