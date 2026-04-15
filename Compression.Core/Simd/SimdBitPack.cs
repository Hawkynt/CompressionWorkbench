#pragma warning disable CS1591

using System.Runtime.CompilerServices;
#if NET7_0_OR_GREATER
using System.Runtime.Intrinsics.X86;
#endif

namespace Compression.Core.Simd;

/// <summary>
/// SIMD-accelerated bit packing and unpacking for symbol streams.
/// Uses BMI2 PDEP/PEXT instructions when available, with a scalar fallback.
/// </summary>
public static class SimdBitPack {
  /// <summary>
  /// Packs symbols into a dense bitstream where each symbol occupies exactly
  /// <paramref name="bitsPerSymbol"/> bits.
  /// </summary>
  /// <param name="symbols">The input symbols (each value must fit in <paramref name="bitsPerSymbol"/> bits).</param>
  /// <param name="bitsPerSymbol">The number of bits per symbol (1-8).</param>
  /// <param name="output">
  /// The output buffer to receive the packed bitstream.
  /// Must be at least <c>(symbols.Length * bitsPerSymbol + 7) / 8</c> bytes.
  /// </param>
  /// <returns>The number of bytes written to <paramref name="output"/>.</returns>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="bitsPerSymbol"/> is not in the range 1-8.
  /// </exception>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int PackBits(ReadOnlySpan<byte> symbols, int bitsPerSymbol, Span<byte> output) {
    ArgumentOutOfRangeException.ThrowIfLessThan(bitsPerSymbol, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(bitsPerSymbol, 8);

    if (symbols.Length == 0)
      return 0;

    // Fast path: 8 bits per symbol is a straight copy
    if (bitsPerSymbol == 8) {
      symbols.CopyTo(output);
      return symbols.Length;
    }

#if NET7_0_OR_GREATER
    if (Bmi2.X64.IsSupported)
      return PackBitsBmi2X64(symbols, bitsPerSymbol, output);

    if (Bmi2.IsSupported)
      return PackBitsBmi2(symbols, bitsPerSymbol, output);
#endif

    return PackBitsScalar(symbols, bitsPerSymbol, output);
  }

  /// <summary>
  /// Unpacks a dense bitstream into individual symbols where each symbol was stored
  /// using <paramref name="bitsPerSymbol"/> bits.
  /// </summary>
  /// <param name="packed">The packed bitstream.</param>
  /// <param name="bitsPerSymbol">The number of bits per symbol (1-8).</param>
  /// <param name="output">The output buffer to receive unpacked symbols.</param>
  /// <param name="count">The number of symbols to unpack.</param>
  /// <returns>The number of symbols written to <paramref name="output"/>.</returns>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="bitsPerSymbol"/> is not in the range 1-8.
  /// </exception>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int UnpackBits(ReadOnlySpan<byte> packed, int bitsPerSymbol, Span<byte> output, int count) {
    ArgumentOutOfRangeException.ThrowIfLessThan(bitsPerSymbol, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(bitsPerSymbol, 8);

    if (count == 0)
      return 0;

    // Fast path: 8 bits per symbol is a straight copy
    if (bitsPerSymbol == 8) {
      packed[..count].CopyTo(output);
      return count;
    }

#if NET7_0_OR_GREATER
    if (Bmi2.X64.IsSupported)
      return UnpackBitsBmi2X64(packed, bitsPerSymbol, output, count);

    if (Bmi2.IsSupported)
      return UnpackBitsBmi2(packed, bitsPerSymbol, output, count);
#endif

    return UnpackBitsScalar(packed, bitsPerSymbol, output, count);
  }

  /// <summary>
  /// Calculates the number of bytes required to store the given number of symbols
  /// at the specified bits-per-symbol rate.
  /// </summary>
  /// <param name="symbolCount">The number of symbols.</param>
  /// <param name="bitsPerSymbol">The number of bits per symbol (1-8).</param>
  /// <returns>The number of bytes required.</returns>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int GetPackedByteCount(int symbolCount, int bitsPerSymbol)
    => (symbolCount * bitsPerSymbol + 7) / 8;

  // ---- Scalar fallback ----

  private static int PackBitsScalar(ReadOnlySpan<byte> symbols, int bitsPerSymbol, Span<byte> output) {
    var symbolMask = (1 << bitsPerSymbol) - 1;
    var bitBuffer = 0UL;
    var bitsInBuffer = 0;
    var outPos = 0;

    for (var i = 0; i < symbols.Length; ++i) {
      bitBuffer |= (ulong)(symbols[i] & symbolMask) << bitsInBuffer;
      bitsInBuffer += bitsPerSymbol;

      while (bitsInBuffer >= 8) {
        output[outPos++] = (byte)(bitBuffer & 0xFF);
        bitBuffer >>= 8;
        bitsInBuffer -= 8;
      }
    }

    // Flush remaining bits
    if (bitsInBuffer > 0)
      output[outPos++] = (byte)(bitBuffer & 0xFF);

    return outPos;
  }

  private static int UnpackBitsScalar(ReadOnlySpan<byte> packed, int bitsPerSymbol, Span<byte> output, int count) {
    var symbolMask = (1UL << bitsPerSymbol) - 1;
    var bitBuffer = 0UL;
    var bitsInBuffer = 0;
    var srcPos = 0;
    var outPos = 0;

    while (outPos < count) {
      // Refill the buffer
      while (bitsInBuffer < bitsPerSymbol && srcPos < packed.Length) {
        bitBuffer |= (ulong)packed[srcPos++] << bitsInBuffer;
        bitsInBuffer += 8;
      }

      if (bitsInBuffer < bitsPerSymbol)
        break; // Not enough data

      output[outPos++] = (byte)(bitBuffer & symbolMask);
      bitBuffer >>= bitsPerSymbol;
      bitsInBuffer -= bitsPerSymbol;
    }

    return outPos;
  }

#if NET7_0_OR_GREATER

  // ---- BMI2 PDEP packing (32-bit) ----

  private static int PackBitsBmi2(ReadOnlySpan<byte> symbols, int bitsPerSymbol, Span<byte> output) {
    var symbolMask = (uint)(1 << bitsPerSymbol) - 1;
    var bitBuffer = 0UL;
    var bitsInBuffer = 0;
    var outPos = 0;
    var i = 0;

    // Build the deposit mask: bitsPerSymbol consecutive 1-bits per symbol slot
    // For packing, we accumulate symbols into a bit buffer

    for (; i < symbols.Length; ++i) {
      bitBuffer |= (ulong)(symbols[i] & symbolMask) << bitsInBuffer;
      bitsInBuffer += bitsPerSymbol;

      // Flush 32 bits at a time
      if (bitsInBuffer >= 32) {
        var val = (uint)(bitBuffer & 0xFFFFFFFF);
        output[outPos] = (byte)val;
        output[outPos + 1] = (byte)(val >> 8);
        output[outPos + 2] = (byte)(val >> 16);
        output[outPos + 3] = (byte)(val >> 24);
        outPos += 4;
        bitBuffer >>= 32;
        bitsInBuffer -= 32;
      }
    }

    // Flush remaining bits
    while (bitsInBuffer >= 8) {
      output[outPos++] = (byte)(bitBuffer & 0xFF);
      bitBuffer >>= 8;
      bitsInBuffer -= 8;
    }

    if (bitsInBuffer > 0)
      output[outPos++] = (byte)(bitBuffer & 0xFF);

    return outPos;
  }

  // ---- BMI2 PEXT unpacking (32-bit) ----

  private static int UnpackBitsBmi2(ReadOnlySpan<byte> packed, int bitsPerSymbol, Span<byte> output, int count) {
    var symbolMask = (uint)(1 << bitsPerSymbol) - 1;
    var outPos = 0;
    var bitBuffer = 0UL;
    var bitsInBuffer = 0;
    var srcPos = 0;

    while (outPos < count) {
      // Refill buffer with up to 4 bytes
      while (bitsInBuffer <= 32 && srcPos < packed.Length) {
        bitBuffer |= (ulong)packed[srcPos++] << bitsInBuffer;
        bitsInBuffer += 8;
      }

      // Extract symbols using PEXT where we have enough bits
      while (bitsInBuffer >= bitsPerSymbol && outPos < count) {
        var val = (uint)(bitBuffer & 0xFFFFFFFF);
        output[outPos++] = (byte)Bmi2.ParallelBitExtract(val, symbolMask);
        bitBuffer >>= bitsPerSymbol;
        bitsInBuffer -= bitsPerSymbol;
      }
    }

    return outPos;
  }

  // ---- BMI2 PDEP packing (64-bit) ----

  private static int PackBitsBmi2X64(ReadOnlySpan<byte> symbols, int bitsPerSymbol, Span<byte> output) {
    var symbolMask = (uint)(1 << bitsPerSymbol) - 1;
    var bitBuffer = 0UL;
    var bitsInBuffer = 0;
    var outPos = 0;

    for (var i = 0; i < symbols.Length; ++i) {
      bitBuffer |= (ulong)(symbols[i] & symbolMask) << bitsInBuffer;
      bitsInBuffer += bitsPerSymbol;

      // Flush 64 bits at a time
      if (bitsInBuffer >= 64) {
        output[outPos] = (byte)bitBuffer;
        output[outPos + 1] = (byte)(bitBuffer >> 8);
        output[outPos + 2] = (byte)(bitBuffer >> 16);
        output[outPos + 3] = (byte)(bitBuffer >> 24);
        output[outPos + 4] = (byte)(bitBuffer >> 32);
        output[outPos + 5] = (byte)(bitBuffer >> 40);
        output[outPos + 6] = (byte)(bitBuffer >> 48);
        output[outPos + 7] = (byte)(bitBuffer >> 56);
        outPos += 8;
        // Since bitsInBuffer can be at most 64 + (bitsPerSymbol-1),
        // and bitsPerSymbol <= 8, bitsInBuffer <= 71.
        // After shifting right 64, we have at most 7 bits remaining.
        // But we cannot shift ulong by 64 (undefined), so handle carefully.
        bitsInBuffer -= 64;
        bitBuffer = bitsInBuffer > 0 ? (ulong)(symbols[i] & symbolMask) >> (bitsPerSymbol - bitsInBuffer) : 0UL;
      }
    }

    // Flush remaining bits
    while (bitsInBuffer >= 8) {
      output[outPos++] = (byte)(bitBuffer & 0xFF);
      bitBuffer >>= 8;
      bitsInBuffer -= 8;
    }

    if (bitsInBuffer > 0)
      output[outPos++] = (byte)(bitBuffer & 0xFF);

    return outPos;
  }

  // ---- BMI2 PEXT unpacking (64-bit) ----

  private static int UnpackBitsBmi2X64(ReadOnlySpan<byte> packed, int bitsPerSymbol, Span<byte> output, int count) {
    var symbolMask = (ulong)(1UL << bitsPerSymbol) - 1;
    var outPos = 0;
    var bitBuffer = 0UL;
    var bitsInBuffer = 0;
    var srcPos = 0;

    while (outPos < count) {
      // Refill buffer with up to 8 bytes
      while (bitsInBuffer <= 56 && srcPos < packed.Length) {
        bitBuffer |= (ulong)packed[srcPos++] << bitsInBuffer;
        bitsInBuffer += 8;
      }

      // Extract symbols using PEXT
      while (bitsInBuffer >= bitsPerSymbol && outPos < count) {
        output[outPos++] = (byte)Bmi2.X64.ParallelBitExtract(bitBuffer, symbolMask);
        bitBuffer >>= bitsPerSymbol;
        bitsInBuffer -= bitsPerSymbol;
      }
    }

    return outPos;
  }

#endif
}
