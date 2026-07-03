using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace Compression.Core.Transforms;

/// <summary>
/// Branch/Call/Jump (BCJ) filters for executable preprocessing.
/// Converts relative branch/call/jump target addresses to absolute addresses,
/// which improves compression by making repeated references to the same function
/// produce identical byte sequences.
/// Supports x86, ARM, ARM Thumb, ARM64, PowerPC, SPARC, IA-64 (Itanium), and RISC-V architectures.
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
    //   Halfword 1 (bytes [i],[i+1]): high 5 bits 11110 → data[i+1] & 0xF8 == 0xF0
    //   Halfword 2 (bytes [i+2],[i+3]): high 5 bits 11111 → data[i+3] & 0xF8 == 0xF8
    // The 22-bit immediate is scaled by 2 (halfword aligned). This is a faithful
    // port of liblzma armthumb.c and is fully bijective on arbitrary data.
    var pos = (uint)startOffset;
    for (var i = 0; i + 3 < data.Length; i += 2) {
      if ((data[i + 1] & 0xF8) != 0xF0 || (data[i + 3] & 0xF8) != 0xF8)
        continue;

      var src = (((uint)data[i + 1] & 7) << 19)
              | ((uint)data[i] << 11)
              | (((uint)data[i + 3] & 7) << 8)
              | data[i + 2];
      src <<= 1;

      uint dest;
      var here = pos + (uint)i + 4;
      if (encode)
        dest = here + src;
      else
        dest = src - here;
      dest >>= 1;

      data[i + 1] = (byte)(0xF0 | ((dest >> 19) & 0x7));
      data[i]     = (byte)(dest >> 11);
      data[i + 3] = (byte)(0xF8 | ((dest >> 8) & 0x7));
      data[i + 2] = (byte)dest;

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

  // -------------------------------------------------------------------------
  // ARM64 / AArch64 BCJ filter (liblzma filter id 0x0A)
  // -------------------------------------------------------------------------

  /// <summary>
  /// Encodes ARM64 (AArch64) machine code by converting relative BL and ADRP
  /// target addresses to absolute addresses. Matches the liblzma arm64 filter.
  /// </summary>
  /// <param name="data">The input data (ARM64 machine code, little-endian words).</param>
  /// <param name="startOffset">The virtual start address of the data. Defaults to 0.</param>
  /// <returns>The filtered data with absolute addresses.</returns>
  public static byte[] EncodeArm64(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformArm64(result, (uint)startOffset, encode: true);
    return result;
  }

  /// <summary>
  /// Decodes ARM64 (AArch64) machine code by converting absolute BL and ADRP
  /// addresses back to relative. Matches the liblzma arm64 filter.
  /// </summary>
  /// <param name="data">The filtered data with absolute addresses.</param>
  /// <param name="startOffset">The virtual start address. Must match the value used during encoding. Defaults to 0.</param>
  /// <returns>The original data with relative addresses restored.</returns>
  public static byte[] DecodeArm64(ReadOnlySpan<byte> data, int startOffset = 0) {
    if (data.Length < 4)
      return data.ToArray();

    var result = data.ToArray();
    TransformArm64(result, (uint)startOffset, encode: false);
    return result;
  }

  private static void TransformArm64(byte[] data, uint nowPos, bool encode) {
    // Port of liblzma src/liblzma/simple/arm64.c (public domain / 0BSD).
    // Words are 4-byte aligned, little-endian.
    var size = data.Length & ~3;
    for (var i = 0; i < size; i += 4) {
      var pc = nowPos + (uint)i;
      var instr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i));

      if ((instr >> 26) == 0x25) {
        // BL: 26-bit word-relative immediate, +/-128 MiB range.
        var src = instr;
        instr = 0x94000000u;

        pc >>= 2;
        if (!encode)
          pc = 0u - pc;

        instr |= (src + pc) & 0x03FFFFFFu;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i), instr);
      } else if ((instr & 0x9F000000u) == 0x90000000u) {
        // ADRP: 21-bit split immediate, page (4 KiB) relative.
        var src = ((instr >> 29) & 3u) | ((instr >> 3) & 0x001FFFFCu);

        // Only convert values within +/-512 MiB (the liblzma range guard).
        if (((src + 0x00020000u) & 0x001C0000u) != 0)
          continue;

        instr &= 0x9000001Fu;

        pc >>= 12;
        if (!encode)
          pc = 0u - pc;

        var dest = src + pc;
        instr |= (dest & 3u) << 29;
        instr |= (dest & 0x0003FFFCu) << 3;
        instr |= (0u - (dest & 0x00020000u)) & 0x00E00000u;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i), instr);
      }
    }
  }

  // -------------------------------------------------------------------------
  // RISC-V BCJ filter (liblzma filter id 0x0B, added in xz 5.6)
  // -------------------------------------------------------------------------

  /// <summary>
  /// Encodes RISC-V machine code by converting JAL and AUIPC+inst2 pc-relative
  /// references to a canonical absolute form. Faithful port of liblzma riscv.c.
  /// </summary>
  /// <param name="data">The input data (RISC-V machine code; always little-endian).</param>
  /// <param name="startOffset">The virtual start address of the data. Rounded down to a multiple of 2. Defaults to 0.</param>
  /// <returns>The filtered data.</returns>
  public static byte[] EncodeRiscV(ReadOnlySpan<byte> data, int startOffset = 0) {
    var result = data.ToArray();
    RiscVEncode(result, (uint)startOffset & ~1u);
    return result;
  }

  /// <summary>
  /// Decodes RISC-V machine code produced by <see cref="EncodeRiscV"/>.
  /// Faithful port of liblzma riscv.c.
  /// </summary>
  /// <param name="data">The filtered data.</param>
  /// <param name="startOffset">The virtual start address. Must match the value used during encoding. Defaults to 0.</param>
  /// <returns>The original data.</returns>
  public static byte[] DecodeRiscV(ReadOnlySpan<byte> data, int startOffset = 0) {
    var result = data.ToArray();
    RiscVDecode(result, (uint)startOffset & ~1u);
    return result;
  }

  // (((auipc) << 8) ^ ((inst2) - 3)) & 0xF8003 — non-zero => not an AUIPC pair.
  private static uint NotAuipcPair(uint auipc, uint inst2)
    => ((auipc << 8) ^ (inst2 - 3)) & 0xF8003u;

  // ((uint)(((auipc) - 0x3117) << 18) >= ((rs1) & 0x1D)) — true => not special format.
  private static bool NotSpecialAuipc(uint auipc, uint rs1)
    => ((auipc - 0x3117u) << 18) >= (rs1 & 0x1Du);

  private static void RiscVEncode(byte[] buffer, uint nowPos) {
    // Port of liblzma src/liblzma/simple/riscv.c riscv_encode (0BSD).
    if (buffer.Length < 8)
      return;

    var size = buffer.Length - 8;
    // The loop steps by 2 bytes because of the C extension (16-bit instructions).
    for (var i = 0; i <= size; i += 2) {
      uint inst = buffer[i];

      if (inst == 0xEF) {
        // JAL — only rd=x1(ra) or rd=x5(t0) are filtered.
        uint b1 = buffer[i + 1];
        if ((b1 & 0x0D) != 0)
          continue;

        uint b2 = buffer[i + 2];
        uint b3 = buffer[i + 3];
        var pc = nowPos + (uint)i;

        var addr = ((b1 & 0xF0u) << 8)
                 | ((b2 & 0x0Fu) << 16)
                 | ((b2 & 0x10u) << 7)
                 | ((b2 & 0xE0u) >> 4)
                 | ((b3 & 0x7Fu) << 4)
                 | ((b3 & 0x80u) << 13);

        addr += pc;

        buffer[i + 1] = (byte)((b1 & 0x0F) | ((addr >> 13) & 0xF0));
        buffer[i + 2] = (byte)(addr >> 9);
        buffer[i + 3] = (byte)(addr >> 1);

        i += 4 - 2;
      } else if ((inst & 0x7F) == 0x17) {
        // AUIPC
        inst |= (uint)buffer[i + 1] << 8;
        inst |= (uint)buffer[i + 2] << 16;
        inst |= (uint)buffer[i + 3] << 24;

        if ((inst & 0xE80) != 0) {
          // AUIPC rd != x0 and != x2.
          var inst2 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 4));

          if (NotAuipcPair(inst, inst2) != 0) {
            i += 6 - 2;
            continue;
          }

          var addr = inst & 0xFFFFF000u;
          addr += (inst2 >> 20) - ((inst2 >> 19) & 0x1000u);
          addr += nowPos + (uint)i;

          inst = 0x17u | (2u << 7) | (inst2 << 12);
          BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i), inst);
          BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(i + 4), addr);
        } else {
          // AUIPC rd == x0 or x2 — fake decoding keeps the filter bijective.
          var fakeRs1 = inst >> 27;

          if (NotSpecialAuipc(inst, fakeRs1)) {
            i += 4 - 2;
            continue;
          }

          var fakeAddr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 4));
          var fakeInst2 = (inst >> 12) | (fakeAddr << 20);
          inst = 0x17u | (fakeRs1 << 7) | (fakeAddr & 0xFFFFF000u);

          BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i), inst);
          BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i + 4), fakeInst2);
        }

        i += 8 - 2;
      }
    }
  }

  private static void RiscVDecode(byte[] buffer, uint nowPos) {
    // Port of liblzma src/liblzma/simple/riscv.c riscv_decode (0BSD).
    if (buffer.Length < 8)
      return;

    var size = buffer.Length - 8;
    for (var i = 0; i <= size; i += 2) {
      uint inst = buffer[i];

      if (inst == 0xEF) {
        // JAL
        uint b1 = buffer[i + 1];
        if ((b1 & 0x0D) != 0)
          continue;

        uint b2 = buffer[i + 2];
        uint b3 = buffer[i + 3];
        var pc = nowPos + (uint)i;

        var addr = ((b1 & 0xF0u) << 13) | (b2 << 9) | (b3 << 1);
        addr -= pc;

        buffer[i + 1] = (byte)((b1 & 0x0F) | ((addr >> 8) & 0xF0));
        buffer[i + 2] = (byte)(((addr >> 16) & 0x0F)
                             | ((addr >> 7) & 0x10)
                             | ((addr << 4) & 0xE0));
        buffer[i + 3] = (byte)(((addr >> 4) & 0x7F) | ((addr >> 13) & 0x80));

        i += 4 - 2;
      } else if ((inst & 0x7F) == 0x17) {
        // AUIPC
        uint inst2;
        inst |= (uint)buffer[i + 1] << 8;
        inst |= (uint)buffer[i + 2] << 16;
        inst |= (uint)buffer[i + 3] << 24;

        if ((inst & 0xE80) != 0) {
          // AUIPC rd != x0 and != x2 — reverse the "fake" pair.
          inst2 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 4));

          if (NotAuipcPair(inst, inst2) != 0) {
            i += 6 - 2;
            continue;
          }

          var addr = inst & 0xFFFFF000u;
          addr += inst2 >> 20;

          inst = 0x17u | (2u << 7) | (inst2 << 12);
          inst2 = addr;
        } else {
          // AUIPC rd == x0 or x2 — reverse the "real" pair.
          var inst2Rs1 = inst >> 27;

          if (NotSpecialAuipc(inst, inst2Rs1)) {
            i += 4 - 2;
            continue;
          }

          var addr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i + 4));
          addr -= nowPos + (uint)i;

          inst2 = (inst >> 12) | (addr << 20);
          inst = 0x17u | (inst2Rs1 << 7) | ((addr + 0x800u) & 0xFFFFF000u);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i), inst);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i + 4), inst2);

        i += 8 - 2;
      }
    }
  }
}
