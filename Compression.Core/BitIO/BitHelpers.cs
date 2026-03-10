using System.Runtime.CompilerServices;

namespace Compression.Core.BitIO;

/// <summary>
/// Shared bit-manipulation utilities.
/// </summary>
internal static class BitHelpers {
  /// <summary>
  /// Reverses the bit order of a code within the specified bit width.
  /// Uses parallel bit-swap (5 stages) to reverse all 32 bits in O(1),
  /// then shifts right to keep only the <paramref name="length"/> MSBs.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal static uint ReverseBits(uint code, int length) {
    code = ((code >> 1) & 0x55555555u) | ((code & 0x55555555u) << 1);  // swap adjacent bits
    code = ((code >> 2) & 0x33333333u) | ((code & 0x33333333u) << 2);  // swap bit pairs
    code = ((code >> 4) & 0x0F0F0F0Fu) | ((code & 0x0F0F0F0Fu) << 4);  // swap nibbles
    code = ((code >> 8) & 0x00FF00FFu) | ((code & 0x00FF00FFu) << 8);  // swap bytes
    code = (code >> 16) | (code << 16);                                  // swap halves
    return code >> (32 - length);
  }
}
