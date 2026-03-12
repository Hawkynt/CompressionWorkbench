using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace Compression.Core.Transforms;

/// <summary>
/// Branch/Call/Jump (BCJ) filters for executable preprocessing.
/// Converts relative branch/call/jump target addresses to absolute addresses,
/// which improves compression by making repeated references to the same function
/// produce identical byte sequences.
/// Supports x86, ARM, ARM Thumb, PowerPC, SPARC, and IA-64 (Itanium) architectures.
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
      var simdLimit = limit - 31;

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
          var pos = i + offset;
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
          for (var b = offset; b < clearEnd; ++b)
            mask &= ~(1u << b);
        }

        i += 32;
      }
    }

    // Scalar tail
    while (i <= limit)
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

  // -------------------------------------------------------------------------
  // ARM (32-bit) BCJ filter
  // -------------------------------------------------------------------------

  /// <summary>
  /// Encodes ARM machine code by converting relative BL (Branch with Link)
  /// instruction offsets to absolute addresses.
  /// </summary>
  /// <param name="data">The input data (ARM machine code).</param>
  /// <param name="startOffset">The virtual start address of the data. Defaults to 0.</param>
  /// <returns>The filtered data with absolute addresses.</returns>
  public static byte[] EncodeArm(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformArm(result, startOffset, encode: true);
    return result;
  }

  /// <summary>
  /// Decodes ARM machine code by converting absolute BL addresses back to relative.
  /// </summary>
  /// <param name="data">The filtered data with absolute addresses.</param>
  /// <param name="startOffset">The virtual start address. Must match the value used during encoding. Defaults to 0.</param>
  /// <returns>The original data with relative addresses restored.</returns>
  public static byte[] DecodeArm(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformArm(result, startOffset, encode: false);
    return result;
  }

  private static void TransformArm(byte[] data, int startOffset, bool encode) {
    // ARM BL instruction: byte[3] == 0xEB, 24-bit signed offset in bytes [0..2] (little-endian)
    // The offset is word-aligned (shifted left 2), giving a ±32 MB range.
    for (var i = 0; i + 3 < data.Length; i += 4) {
      if (data[i + 3] != 0xEB)
        continue;

      // Extract 24-bit signed offset (little-endian in ARM encoding)
      var offset = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
      // Sign-extend from 24 bits
      if ((offset & 0x800000) != 0)
        offset |= unchecked((int)0xFF000000);

      var currentAddr = (startOffset + i) >> 2; // word address
      if (encode)
        offset += currentAddr;
      else
        offset -= currentAddr;

      // Write back lower 24 bits
      data[i]     = (byte)(offset & 0xFF);
      data[i + 1] = (byte)((offset >> 8) & 0xFF);
      data[i + 2] = (byte)((offset >> 16) & 0xFF);
    }
  }

  // -------------------------------------------------------------------------
  // ARM Thumb BCJ filter
  // -------------------------------------------------------------------------

  /// <summary>
  /// Encodes ARM Thumb machine code by converting relative BL (Branch with Link)
  /// instruction offsets to absolute addresses.
  /// </summary>
  /// <param name="data">The input data (ARM Thumb machine code).</param>
  /// <param name="startOffset">The virtual start address of the data. Defaults to 0.</param>
  /// <returns>The filtered data with absolute addresses.</returns>
  public static byte[] EncodeArmThumb(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformArmThumb(result, startOffset, encode: true);
    return result;
  }

  /// <summary>
  /// Decodes ARM Thumb machine code by converting absolute BL addresses back to relative.
  /// </summary>
  /// <param name="data">The filtered data with absolute addresses.</param>
  /// <param name="startOffset">The virtual start address. Must match the value used during encoding. Defaults to 0.</param>
  /// <returns>The original data with relative addresses restored.</returns>
  public static byte[] DecodeArmThumb(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformArmThumb(result, startOffset, encode: false);
    return result;
  }

  private static void TransformArmThumb(byte[] data, int startOffset, bool encode) {
    // Thumb BL is a 32-bit instruction encoded as two 16-bit halfwords (little-endian):
    //   Halfword 1 (bytes [i],[i+1]): 0xF000-0xF7FF → bits 15..11 = 11110, imm10 in bits 10..0
    //   Halfword 2 (bytes [i+2],[i+3]): 0xF800-0xFFFF → bits 15..11 = 11111, imm11 in bits 10..0
    // Combined 21-bit offset = (imm10 << 11) | imm11, shifted left 1 for halfword alignment.
    for (var i = 0; i + 3 < data.Length; i += 2) {
      var hw1 = data[i] | (data[i + 1] << 8);
      var hw2 = data[i + 2] | (data[i + 3] << 8);

      if ((hw1 & 0xF800) != 0xF000 || (hw2 & 0xF800) != 0xF800)
        continue;

      var imm10 = hw1 & 0x7FF;
      var imm11 = hw2 & 0x7FF;
      var offset = (imm10 << 11) | imm11;

      // Sign-extend from 21 bits
      if ((offset & 0x100000) != 0)
        offset |= unchecked((int)0xFFE00000);

      var currentAddr = (startOffset + i) >> 1; // halfword address
      if (encode)
        offset += currentAddr;
      else
        offset -= currentAddr;

      // Write back
      data[i]     = (byte)(((offset >> 11) & 0xFF));
      data[i + 1] = (byte)(0xF0 | (((offset >> 11) >> 8) & 0x07));
      data[i + 2] = (byte)((offset & 0xFF));
      data[i + 3] = (byte)(0xF8 | ((offset >> 8) & 0x07));

      i += 2; // skip past the second halfword (loop adds 2)
    }
  }

  // -------------------------------------------------------------------------
  // PowerPC BCJ filter
  // -------------------------------------------------------------------------

  /// <summary>
  /// Encodes PowerPC machine code by converting relative B/BL (Branch/Branch with Link)
  /// instruction offsets to absolute addresses.
  /// </summary>
  /// <param name="data">The input data (PowerPC machine code, big-endian).</param>
  /// <param name="startOffset">The virtual start address of the data. Defaults to 0.</param>
  /// <returns>The filtered data with absolute addresses.</returns>
  public static byte[] EncodePowerPC(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformPowerPC(result, startOffset, encode: true);
    return result;
  }

  /// <summary>
  /// Decodes PowerPC machine code by converting absolute B/BL addresses back to relative.
  /// </summary>
  /// <param name="data">The filtered data with absolute addresses.</param>
  /// <param name="startOffset">The virtual start address. Must match the value used during encoding. Defaults to 0.</param>
  /// <returns>The original data with relative addresses restored.</returns>
  public static byte[] DecodePowerPC(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformPowerPC(result, startOffset, encode: false);
    return result;
  }

  private static void TransformPowerPC(byte[] data, int startOffset, bool encode) {
    // PowerPC B/BL instruction (big-endian, 4 bytes):
    //   Opcode in bits 0-5 = 18 (010010)
    //   LI field in bits 6-29 = 24-bit signed offset (word-aligned, shifted left 2)
    //   AA bit 30 = 0 (relative)
    //   LK bit 31 = 1 (link register = BL)
    // Mask: (instr & 0xFC000003) == 0x48000001
    for (var i = 0; i + 3 < data.Length; i += 4) {
      var instr = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
      if ((instr & 0xFC000003u) != 0x48000001u)
        continue;

      // Extract 26-bit signed offset (already includes the shift-left-2)
      var offset = (int)(instr & 0x03FFFFFCu);
      // Sign-extend from 26 bits
      if ((offset & 0x02000000) != 0)
        offset |= unchecked((int)0xFC000000);

      if (encode)
        offset += startOffset + i;
      else
        offset -= startOffset + i;

      // Write back: preserve opcode (bits 0-5) and LK (bit 31), replace LI + AA
      instr = (instr & 0xFC000003u) | ((uint)offset & 0x03FFFFFCu);
      BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i), instr);
    }
  }

  // -------------------------------------------------------------------------
  // SPARC BCJ filter
  // -------------------------------------------------------------------------

  /// <summary>
  /// Encodes SPARC machine code by converting relative CALL instruction offsets
  /// to absolute addresses.
  /// </summary>
  /// <param name="data">The input data (SPARC machine code, big-endian).</param>
  /// <param name="startOffset">The virtual start address of the data. Defaults to 0.</param>
  /// <returns>The filtered data with absolute addresses.</returns>
  public static byte[] EncodeSparc(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformSparc(result, startOffset, encode: true);
    return result;
  }

  /// <summary>
  /// Decodes SPARC machine code by converting absolute CALL addresses back to relative.
  /// </summary>
  /// <param name="data">The filtered data with absolute addresses.</param>
  /// <param name="startOffset">The virtual start address. Must match the value used during encoding. Defaults to 0.</param>
  /// <returns>The original data with relative addresses restored.</returns>
  public static byte[] DecodeSparc(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformSparc(result, startOffset, encode: false);
    return result;
  }

  private static void TransformSparc(byte[] data, int startOffset, bool encode) {
    // SPARC CALL instruction (big-endian, 4 bytes):
    //   Bits 31-30 = 01 (format 1 = CALL)
    //   Bits 29-0  = 30-bit word-aligned displacement (shifted left 2 gives byte offset)
    for (var i = 0; i + 3 < data.Length; i += 4) {
      var instr = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
      if ((instr >> 30) != 1)
        continue;

      // Extract 30-bit signed displacement (word-aligned)
      var disp = (int)(instr & 0x3FFFFFFFu);
      // Sign-extend from 30 bits
      if ((disp & 0x20000000) != 0)
        disp |= unchecked((int)0xC0000000);

      var currentWord = (startOffset + i) >> 2;
      if (encode)
        disp += currentWord;
      else
        disp -= currentWord;

      // Write back: preserve format bits (31-30 = 01)
      instr = 0x40000000u | ((uint)disp & 0x3FFFFFFFu);
      BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i), instr);
    }
  }

  // -------------------------------------------------------------------------
  // IA-64 (Itanium) BCJ filter
  // -------------------------------------------------------------------------

  /// <summary>
  /// Encodes IA-64 (Itanium) machine code by converting relative branch target
  /// addresses to absolute addresses within 128-bit instruction bundles.
  /// </summary>
  /// <param name="data">The input data (IA-64 machine code).</param>
  /// <param name="startOffset">The virtual start address of the data. Defaults to 0.</param>
  /// <returns>The filtered data with absolute addresses.</returns>
  public static byte[] EncodeIA64(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 16)
      return data.ToArray();

    var result = data.ToArray();
    TransformIA64(result, startOffset, encode: true);
    return result;
  }

  /// <summary>
  /// Decodes IA-64 (Itanium) machine code by converting absolute branch target
  /// addresses back to relative addresses within 128-bit instruction bundles.
  /// </summary>
  /// <param name="data">The filtered data with absolute addresses.</param>
  /// <param name="startOffset">The virtual start address. Must match the value used during encoding. Defaults to 0.</param>
  /// <returns>The original data with relative addresses restored.</returns>
  public static byte[] DecodeIA64(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 16)
      return data.ToArray();

    var result = data.ToArray();
    TransformIA64(result, startOffset, encode: false);
    return result;
  }

  /// <summary>
  /// Branch slot mask lookup table indexed by template (0-31).
  /// Bit 0 = slot 0 is B-type, bit 1 = slot 1 is B-type, bit 2 = slot 2 is B-type.
  /// </summary>
  private static ReadOnlySpan<byte> IA64BranchSlotMask =>
  [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    4, 4, 6, 6, 0, 0, 7, 7, 4, 4, 0, 0, 4, 4, 0, 0,
  ];

  private static void TransformIA64(byte[] data, int startOffset, bool encode) {
    var mask = BcjFilter.IA64BranchSlotMask;

    for (var pos = 0; pos + 15 < data.Length; pos += 16) {
      var templateByte = data[pos] & 0x1F;
      int slotMask = mask[templateByte];
      if (slotMask == 0)
        continue;

      for (var slot = 0; slot < 3; ++slot) {
        if ((slotMask & (1 << slot)) == 0)
          continue;

        // Each slot is 41 bits. Slot 0 starts at bit 5, slot 1 at bit 46, slot 2 at bit 87.
        var bitOffset = 5 + 41 * slot;

        // Extract 41-bit instruction from the 128-bit bundle
        var instr = ExtractIA64Bits(data, pos, bitOffset, 41);

        // Check major opcode (bits 37-40 of the 41-bit instruction) == 4
        var opcode = (uint)((instr >> 37) & 0xF);
        if (opcode != 4)
          continue;

        // Extract imm20b (bits 13-32) and sign bit (bit 36)
        var imm20b = (uint)((instr >> 13) & 0xFFFFF);
        var signBit = (uint)((instr >> 36) & 1);

        // Construct 25-bit byte offset: (signBit << 20 | imm20b) << 4
        var target = (int)((signBit << 20) | imm20b);
        // Sign-extend from 21 bits
        if ((target & 0x100000) != 0)
          target |= unchecked((int)0xFFE00000);
        target <<= 4;

        if (encode)
          target += startOffset + pos;
        else
          target -= startOffset + pos;

        // Write back: imm20b = (target >> 4) & 0xFFFFF, signBit = (target >> 24) & 1
        imm20b = (uint)((target >> 4) & 0xFFFFF);
        signBit = (uint)((target >> 24) & 1);

        // Rebuild instruction with modified bits
        instr &= ~((0xFFFFFUL << 13) | (1UL << 36));
        instr |= (ulong)imm20b << 13;
        instr |= (ulong)signBit << 36;

        InsertIA64Bits(data, pos, bitOffset, 41, instr);
      }
    }
  }

  private static ulong ExtractIA64Bits(byte[] data, int bundlePos, int bitOffset, int bitCount) {
    ulong result = 0;
    for (var i = 0; i < bitCount; ++i) {
      var absBit = bitOffset + i;
      var byteIdx = bundlePos + (absBit >> 3);
      var bitIdx = absBit & 7;
      if ((data[byteIdx] & (1 << bitIdx)) != 0)
        result |= 1UL << i;
    }
    return result;
  }

  private static void InsertIA64Bits(byte[] data, int bundlePos, int bitOffset, int bitCount, ulong value) {
    for (var i = 0; i < bitCount; ++i) {
      var absBit = bitOffset + i;
      var byteIdx = bundlePos + (absBit >> 3);
      var bitIdx = absBit & 7;
      if ((value & (1UL << i)) != 0)
        data[byteIdx] |= (byte)(1 << bitIdx);
      else
        data[byteIdx] &= (byte)~(1 << bitIdx);
    }
  }
}
