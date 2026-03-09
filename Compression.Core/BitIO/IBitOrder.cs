using System.Runtime.CompilerServices;

namespace Compression.Core.BitIO;

/// <summary>
/// Static-abstract strategy for bit ordering. Implement as a zero-size struct
/// so generic specialization eliminates all branching at JIT time.
/// </summary>
public interface IBitOrder {
  /// <summary>Inserts a byte into the read accumulator.</summary>
  static abstract ulong InsertByte(ulong buffer, int bitsInBuffer, int b);

  /// <summary>Extracts a single bit from the read byte-buffer and advances.</summary>
  static abstract (int Bit, int Buffer) ExtractBit(int buffer);

  /// <summary>Peeks <paramref name="count"/> bits from the accumulator without consuming.</summary>
  static abstract uint Peek(ulong buffer, int bitsInBuffer, int count);

  /// <summary>Drops <paramref name="count"/> bits from the accumulator.</summary>
  static abstract ulong Drop(ulong buffer, int bitsInBuffer, int count);

  /// <summary>Places a single bit into the write byte-buffer.</summary>
  static abstract int PlaceBit(int buffer, int bitsInBuffer, int bit);

  /// <summary>Accumulates a decoded bit into <paramref name="result"/> at position <paramref name="index"/>.</summary>
  static abstract uint AccumulateBits(uint result, int bit, int index);

  /// <summary>Returns the bit shift for the <paramref name="index"/>-th bit during multi-bit write of <paramref name="count"/> bits.</summary>
  static abstract int WriteBitIndex(int count, int index);
}

/// <summary>
/// LSB-first bit ordering. Used by Deflate, ZIP, GZIP, PNG.
/// Zero-size struct — no runtime cost.
/// </summary>
public readonly struct LsbBitOrder : IBitOrder {
  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ulong InsertByte(ulong buffer, int bitsInBuffer, int b) =>
    buffer | ((ulong)b << bitsInBuffer);

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static (int Bit, int Buffer) ExtractBit(int buffer) =>
    (buffer & 1, buffer >> 1);

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static uint Peek(ulong buffer, int bitsInBuffer, int count) =>
    (uint)(buffer & ((1UL << count) - 1));

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ulong Drop(ulong buffer, int bitsInBuffer, int count) =>
    buffer >> count;

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int PlaceBit(int buffer, int bitsInBuffer, int bit) =>
    buffer | ((bit & 1) << bitsInBuffer);

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static uint AccumulateBits(uint result, int bit, int index) =>
    result | ((uint)bit << index);

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int WriteBitIndex(int count, int index) => index;
}

/// <summary>
/// MSB-first bit ordering. Used by JPEG, bzip2, many legacy formats.
/// Zero-size struct — no runtime cost.
/// </summary>
public readonly struct MsbBitOrder : IBitOrder {
  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ulong InsertByte(ulong buffer, int bitsInBuffer, int b) =>
    (buffer << 8) | (uint)(byte)b;

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static (int Bit, int Buffer) ExtractBit(int buffer) =>
    ((buffer >> 7) & 1, (buffer << 1) & 0xFF);

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static uint Peek(ulong buffer, int bitsInBuffer, int count) =>
    (uint)(buffer >> (bitsInBuffer - count)) & (uint)((1UL << count) - 1);

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static ulong Drop(ulong buffer, int bitsInBuffer, int count) =>
    buffer & ((1UL << (bitsInBuffer - count)) - 1);

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int PlaceBit(int buffer, int bitsInBuffer, int bit) =>
    buffer | ((bit & 1) << (7 - bitsInBuffer));

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static uint AccumulateBits(uint result, int bit, int index) =>
    (result << 1) | (uint)bit;

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int WriteBitIndex(int count, int index) => count - 1 - index;
}
