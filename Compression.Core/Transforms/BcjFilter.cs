using System.Buffers.Binary;

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

    byte[] result = new byte[data.Length];
    data.CopyTo(result);

    int i = 0;
    while (i < result.Length - 4) {
      if (result[i] == 0xE8 || result[i] == 0xE9) {
        int relative = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(i + 1));
        int absolute = relative + (startOffset + i + 5);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(i + 1), absolute);
        i += 5;
      }
      else
        ++i;
    }

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

    byte[] result = new byte[data.Length];
    data.CopyTo(result);

    int i = 0;
    while (i < result.Length - 4) {
      if (result[i] == 0xE8 || result[i] == 0xE9) {
        int absolute = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(i + 1));
        int relative = absolute - (startOffset + i + 5);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(i + 1), relative);
        i += 5;
      }
      else
        ++i;
    }

    return result;
  }
}
