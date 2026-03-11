using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace Compression.Core.Transforms;

/// <summary>
/// Branch/Call/Jump (BCJ) filter for x86 executable preprocessing.
/// Converts relative CALL (0xE8) and JMP (0xE9) target addresses to absolute addresses,
/// which improves compression by making repeated references to the same function
/// produce identical byte sequences.
/// </summary>
public static class BcjFilter {
  /// <summary>
  /// Encodes x86 machine code by converting relative CALL/JMP addresses to absolute.
  /// </summary>
  /// <param name="data">The input data (typically x86 machine code).</param>
  /// <param name="startOffset">The virtual start address of the data. Defaults to 0.</param>
  /// <returns>The filtered data with absolute addresses.</returns>
  public static byte[] EncodeX86(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length == 0)
      return [];

    var result = new byte[data.Length];
    data.CopyTo(result);

    TransformX86(result, startOffset, encode: true);

    return result;
  }

  /// <summary>
  /// Decodes x86 machine code by converting absolute CALL/JMP addresses back to relative.
  /// </summary>
  /// <param name="data">The filtered data with absolute addresses.</param>
  /// <param name="startOffset">The virtual start address of the data. Must match the value used during encoding. Defaults to 0.</param>
  /// <returns>The original data with relative addresses restored.</returns>
  public static byte[] DecodeX86(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length == 0)
      return [];

    var result = new byte[data.Length];
    data.CopyTo(result);

    TransformX86(result, startOffset, encode: false);

    return result;
  }

  private static void TransformX86(byte[] result, int startOffset, bool encode) {
    var limit = result.Length - 4;
    var i = 0;

    if (Vector256.IsHardwareAccelerated && limit >= 32) {
      var e8 = Vector256.Create((byte)0xE8);
      var e9 = Vector256.Create((byte)0xE9);
      int simdLimit = limit - 31;

      while (i < simdLimit) {
        var chunk = Vector256.Create<byte>(result.AsSpan(i));
        var matchE8 = Vector256.Equals(chunk, e8);
        var matchE9 = Vector256.Equals(chunk, e9);
        var match = matchE8 | matchE9;

        var mask = match.ExtractMostSignificantBits();
        if (mask == 0) {
          i += 32;
          continue;
        }

        // Process matches within this 32-byte window
        while (mask != 0) {
          var offset = BitOperations.TrailingZeroCount(mask);
          int pos = i + offset;
          if (pos > limit)
            break;

          var addr = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(pos + 1));
          if (encode)
            addr += startOffset + pos + 5;
          else
            addr -= startOffset + pos + 5;

          BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos + 1), addr);

          // Clear this bit and the next 4 bits (skip the address bytes)
          // We need to skip to pos+5, clear bits up to offset+4
          var clearEnd = Math.Min(offset + 5, 32);
          for (int b = offset; b < clearEnd; ++b)
            mask &= ~(1u << b);
        }

        i += 32;
      }
    }

    // Scalar tail
    while (i <= limit) {
      if (result[i] == 0xE8 || result[i] == 0xE9) {
        var addr = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(i + 1));
        if (encode)
          addr += startOffset + i + 5;
        else
          addr -= startOffset + i + 5;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(i + 1), addr);
        i += 5;
      }
      else
        ++i;
    }
  }
}
