using System.Buffers.Binary;

namespace Compression.Core.Transforms;

/// <summary>
/// The branch-call-jump converters of the 7z and xz formats in their streaming form: each
/// instance carries the running position and whatever state the instruction set needs across
/// buffer boundaries, so a stream of any length is converted piece by piece with bounded memory.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Convert"/> converts as much of the buffer as it can decide on and returns how many
/// leading bytes are final. The caller keeps the rest, appends more input, and calls again; at the
/// end of the stream the bytes that never became final are passed through unchanged, exactly as
/// the reference coders do.
/// </para>
/// <para>
/// Converted from the LZMA SDK by Igor Pavlov (C/Bra.c, C/Bra86.c, C/BraIA64.c; public domain) and,
/// for ARM64 and RISC-V, from xz Utils by Lasse Collin and Igor Pavlov
/// (src/liblzma/simple/arm64.c, riscv.c; public domain / 0BSD). See THIRD-PARTY-NOTICE.lzma-sdk.txt.
/// </para>
/// </remarks>
public abstract class BranchConverter {
  private uint _position;

  /// <summary>Whether this instance restores the original code (decoding) rather than filtering it.</summary>
  protected bool Decoding { get; }

  /// <summary>Initializes a converter starting at virtual address <paramref name="startOffset"/>.</summary>
  protected BranchConverter(bool decoding, uint startOffset) {
    this.Decoding = decoding;
    this._position = startOffset;
  }

  /// <summary>
  /// Converts <paramref name="data"/> in place and returns the number of leading bytes that are
  /// final; the remainder must be presented again, followed by the next input.
  /// </summary>
  public int Convert(Span<byte> data) {
    var done = this.ConvertCore(data, this._position);
    this._position += (uint)done;
    return done;
  }

  /// <summary>Converts <paramref name="data"/>, which starts at virtual address <paramref name="ip"/>.</summary>
  protected abstract int ConvertCore(Span<byte> data, uint ip);

  /// <summary>The 7z coder or xz filter this converter implements, by its 7z method name.</summary>
  public static BranchConverter Create(BranchArchitecture architecture, bool decoding, uint startOffset = 0) => architecture switch {
    BranchArchitecture.X86 => new X86(decoding, startOffset),
    BranchArchitecture.PowerPC => new PowerPC(decoding, startOffset),
    BranchArchitecture.IA64 => new IA64(decoding, startOffset),
    BranchArchitecture.Arm => new Arm(decoding, startOffset),
    BranchArchitecture.ArmThumb => new ArmThumb(decoding, startOffset),
    BranchArchitecture.Sparc => new Sparc(decoding, startOffset),
    BranchArchitecture.Arm64 => new Arm64(decoding, startOffset),
    BranchArchitecture.RiscV => new RiscV(decoding, startOffset),
    _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
  };

  /// <summary>Converts a whole buffer in place, leaving the undecidable tail as it is.</summary>
  public static void ConvertAll(BranchArchitecture architecture, Span<byte> data, bool decoding, uint startOffset = 0)
    => Create(architecture, decoding, startOffset).Convert(data);

  private static bool IsX86MsByte(byte b) => b is 0x00 or 0xFF;

  /// <summary>x86 CALL (E8) / JMP (E9) converter with the prefix-mask state of Bra86.c.</summary>
  private sealed class X86(bool decoding, uint startOffset) : BranchConverter(decoding, startOffset) {
    private static ReadOnlySpan<byte> MaskToAllowed => [1, 1, 1, 0, 1, 0, 0, 0];
    private static ReadOnlySpan<byte> MaskToBitNumber => [0, 1, 2, 2, 3, 3, 3, 3];
    private uint _prevMask;

    protected override int ConvertCore(Span<byte> data, uint ip) {
      if (data.Length < 5)
        return 0;

      var encoding = !this.Decoding;
      var prevMask = this._prevMask & 7;
      ip += 5;
      var bufferPos = 0;
      var prevPos = -1;
      var limit = data.Length - 4;

      for (;;) {
        var p = bufferPos;
        while (p < limit && (data[p] & 0xFE) != 0xE8)
          ++p;
        bufferPos = p;
        if (p >= limit)
          break;

        var distance = bufferPos - prevPos;
        if (distance > 3)
          prevMask = 0;
        else {
          prevMask = (prevMask << (distance - 1)) & 7;
          if (prevMask != 0) {
            var b = data[p + 4 - MaskToBitNumber[(int)prevMask]];
            if (MaskToAllowed[(int)prevMask] == 0 || IsX86MsByte(b)) {
              prevPos = bufferPos;
              prevMask = ((prevMask << 1) & 7) | 1;
              ++bufferPos;
              continue;
            }
          }
        }
        prevPos = bufferPos;

        if (IsX86MsByte(data[p + 4])) {
          var src = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 1)..]);
          uint dest;
          for (;;) {
            dest = encoding ? ip + (uint)bufferPos + src : src - (ip + (uint)bufferPos);
            if (prevMask == 0)
              break;

            var index = MaskToBitNumber[(int)prevMask] * 8;
            var b = (byte)(dest >> (24 - index));
            if (!IsX86MsByte(b))
              break;

            src = dest ^ ((1u << (32 - index)) - 1);
          }

          data[p + 4] = (byte)~(((dest >> 24) & 1) - 1);
          data[p + 3] = (byte)(dest >> 16);
          data[p + 2] = (byte)(dest >> 8);
          data[p + 1] = (byte)dest;
          bufferPos += 5;
        } else {
          prevMask = ((prevMask << 1) & 7) | 1;
          ++bufferPos;
        }
      }

      var tail = bufferPos - prevPos;
      this._prevMask = tail > 3 ? 0 : (prevMask << (tail - 1)) & 7;
      return bufferPos;
    }
  }

  /// <summary>ARM BL: 24-bit word displacement, the PC reading eight bytes ahead.</summary>
  private sealed class Arm(bool decoding, uint startOffset) : BranchConverter(decoding, startOffset) {
    protected override int ConvertCore(Span<byte> data, uint ip) {
      var size = data.Length & ~3;
      ip += 8;
      for (var i = 0; i < size; i += 4) {
        if (data[i + 3] != 0xEB)
          continue;

        var src = ((uint)data[i + 2] << 16 | (uint)data[i + 1] << 8 | data[i]) << 2;
        var dest = (this.Decoding ? src - (ip + (uint)i) : ip + (uint)i + src) >> 2;
        data[i + 2] = (byte)(dest >> 16);
        data[i + 1] = (byte)(dest >> 8);
        data[i] = (byte)dest;
      }
      return size;
    }
  }

  /// <summary>ARM Thumb BL pairs: 22-bit half-word displacement, the PC reading four bytes ahead.</summary>
  private sealed class ArmThumb(bool decoding, uint startOffset) : BranchConverter(decoding, startOffset) {
    protected override int ConvertCore(Span<byte> data, uint ip) {
      if (data.Length < 4)
        return 0;

      var last = data.Length - 4;
      ip += 4;
      var i = 0;
      for (; i <= last; i += 2) {
        if ((data[i + 1] & 0xF8) != 0xF0 || (data[i + 3] & 0xF8) != 0xF8)
          continue;

        var src = (((uint)data[i + 1] & 7) << 19 | (uint)data[i] << 11 | ((uint)data[i + 3] & 7) << 8 | data[i + 2]) << 1;
        var dest = (this.Decoding ? src - (ip + (uint)i) : ip + (uint)i + src) >> 1;
        data[i + 1] = (byte)(0xF0 | ((dest >> 19) & 7));
        data[i] = (byte)(dest >> 11);
        data[i + 3] = (byte)(0xF8 | ((dest >> 8) & 7));
        data[i + 2] = (byte)dest;
        i += 2;
      }
      return i;
    }
  }

  /// <summary>PowerPC "bl": big-endian, opcode 18 with AA=0 and LK=1.</summary>
  private sealed class PowerPC(bool decoding, uint startOffset) : BranchConverter(decoding, startOffset) {
    protected override int ConvertCore(Span<byte> data, uint ip) {
      var size = data.Length & ~3;
      for (var i = 0; i < size; i += 4) {
        if ((data[i] >> 2) != 0x12 || (data[i + 3] & 3) != 1)
          continue;

        var src = ((uint)data[i] & 3) << 24 | (uint)data[i + 1] << 16 | (uint)data[i + 2] << 8 | ((uint)data[i + 3] & ~3u);
        var dest = this.Decoding ? src - (ip + (uint)i) : ip + (uint)i + src;
        data[i] = (byte)(0x48 | ((dest >> 24) & 3));
        data[i + 1] = (byte)(dest >> 16);
        data[i + 2] = (byte)(dest >> 8);
        data[i + 3] = (byte)((data[i + 3] & 3) | (byte)dest);
      }
      return size;
    }
  }

  /// <summary>SPARC CALL whose 30-bit displacement fits in 23 signed bits.</summary>
  private sealed class Sparc(bool decoding, uint startOffset) : BranchConverter(decoding, startOffset) {
    protected override int ConvertCore(Span<byte> data, uint ip) {
      var size = data.Length & ~3;
      for (var i = 0; i < size; i += 4) {
        if (!((data[i] == 0x40 && (data[i + 1] & 0xC0) == 0x00) || (data[i] == 0x7F && (data[i + 1] & 0xC0) == 0xC0)))
          continue;

        var src = BinaryPrimitives.ReadUInt32BigEndian(data[i..]) << 2;
        var dest = (this.Decoding ? src - (ip + (uint)i) : ip + (uint)i + src) >> 2;
        dest = (((0 - ((dest >> 22) & 1)) << 22) & 0x3FFFFFFF) | (dest & 0x3FFFFF) | 0x40000000;
        BinaryPrimitives.WriteUInt32BigEndian(data[i..], dest);
      }
      return size;
    }
  }

  /// <summary>IA-64 bundles: the branch slots the template names, opcode 5 with btype 0.</summary>
  private sealed class IA64(bool decoding, uint startOffset) : BranchConverter(decoding, startOffset) {
    private static ReadOnlySpan<byte> BranchTable => [
      0, 0, 0, 0, 0, 0, 0, 0,
      0, 0, 0, 0, 0, 0, 0, 0,
      4, 4, 6, 6, 0, 0, 7, 7,
      4, 4, 0, 0, 4, 4, 0, 0,
    ];

    protected override int ConvertCore(Span<byte> data, uint ip) {
      var size = data.Length & ~15;
      for (var i = 0; i < size; i += 16) {
        var mask = BranchTable[data[i] & 0x1F];
        var bitPos = 5;
        for (var slot = 0; slot < 3; ++slot, bitPos += 41) {
          if (((mask >> slot) & 1) == 0)
            continue;

          var bytePos = i + (bitPos >> 3);
          var bitRes = bitPos & 7;
          var instruction = 0UL;
          for (var j = 0; j < 6; ++j)
            instruction |= (ulong)data[bytePos + j] << (8 * j);

          var norm = instruction >> bitRes;
          if (((norm >> 37) & 0xF) != 0x5 || ((norm >> 9) & 0x7) != 0)
            continue;

          var src = (uint)((norm >> 13) & 0xFFFFF) | ((uint)(norm >> 36) & 1) << 20;
          src <<= 4;
          var dest = (this.Decoding ? src - (ip + (uint)i) : ip + (uint)i + src) >> 4;
          norm &= ~(0x8FFFFFUL << 13);
          norm |= (ulong)(dest & 0xFFFFF) << 13;
          norm |= (ulong)(dest & 0x100000) << (36 - 20);
          instruction &= (1UL << bitRes) - 1;
          instruction |= norm << bitRes;
          for (var j = 0; j < 6; ++j)
            data[bytePos + j] = (byte)(instruction >> (8 * j));
        }
      }
      return size;
    }
  }

  /// <summary>ARM64 BL and ADRP (xz / 7-Zip filter id 0x0A).</summary>
  private sealed class Arm64(bool decoding, uint startOffset) : BranchConverter(decoding, startOffset) {
    protected override int ConvertCore(Span<byte> data, uint ip) {
      var size = data.Length & ~3;
      for (var i = 0; i < size; i += 4) {
        var pc = ip + (uint)i;
        var instr = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
        if ((instr >> 26) == 0x25) {
          var src = instr;
          pc >>= 2;
          if (this.Decoding)
            pc = 0u - pc;
          BinaryPrimitives.WriteUInt32LittleEndian(data[i..], 0x94000000u | ((src + pc) & 0x03FFFFFFu));
        } else if ((instr & 0x9F000000u) == 0x90000000u) {
          var src = ((instr >> 29) & 3u) | ((instr >> 3) & 0x001FFFFCu);
          if (((src + 0x00020000u) & 0x001C0000u) != 0)
            continue;

          instr &= 0x9000001Fu;
          pc >>= 12;
          if (this.Decoding)
            pc = 0u - pc;
          var dest = src + pc;
          instr |= (dest & 3u) << 29;
          instr |= (dest & 0x0003FFFCu) << 3;
          instr |= (0u - (dest & 0x00020000u)) & 0x00E00000u;
          BinaryPrimitives.WriteUInt32LittleEndian(data[i..], instr);
        }
      }
      return size;
    }
  }

  /// <summary>RISC-V JAL and AUIPC pairs (xz / 7-Zip filter id 0x0B).</summary>
  private sealed class RiscV(bool decoding, uint startOffset) : BranchConverter(decoding, startOffset & ~1u) {
    // Non-zero when (auipc, inst2) is not an AUIPC + I/S-type pair using the same register.
    private static uint NotAuipcPair(uint auipc, uint inst2) => ((auipc << 8) ^ (inst2 - 3)) & 0xF8003u;

    // True when an AUIPC with rd = x0 or x2 is not in the special form the encoder produces.
    private static bool NotSpecialAuipc(uint auipc, uint rs1) => ((auipc - 0x3117u) << 18) >= (rs1 & 0x1Du);

    protected override int ConvertCore(Span<byte> data, uint ip) => this.Decoding ? Decode(data, ip) : Encode(data, ip);

    private static int Encode(Span<byte> buffer, uint nowPos) {
      if (buffer.Length < 8)
        return 0;

      var size = buffer.Length - 8;
      var i = 0;
      for (; i <= size; i += 2) {
        uint inst = buffer[i];
        if (inst == 0xEF) {
          uint b1 = buffer[i + 1];
          if ((b1 & 0x0D) != 0)
            continue;

          uint b2 = buffer[i + 2];
          uint b3 = buffer[i + 3];
          var addr = ((b1 & 0xF0u) << 8) | ((b2 & 0x0Fu) << 16) | ((b2 & 0x10u) << 7) | ((b2 & 0xE0u) >> 4) | ((b3 & 0x7Fu) << 4) | ((b3 & 0x80u) << 13);
          addr += nowPos + (uint)i;
          buffer[i + 1] = (byte)((b1 & 0x0F) | ((addr >> 13) & 0xF0));
          buffer[i + 2] = (byte)(addr >> 9);
          buffer[i + 3] = (byte)(addr >> 1);
          i += 4 - 2;
        } else if ((inst & 0x7F) == 0x17) {
          inst |= (uint)buffer[i + 1] << 8 | (uint)buffer[i + 2] << 16 | (uint)buffer[i + 3] << 24;
          if ((inst & 0xE80) != 0) {
            var inst2 = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(i + 4)..]);
            if (NotAuipcPair(inst, inst2) != 0) {
              i += 6 - 2;
              continue;
            }

            var addr = inst & 0xFFFFF000u;
            addr += (inst2 >> 20) - ((inst2 >> 19) & 0x1000u);
            addr += nowPos + (uint)i;
            inst = 0x17u | (2u << 7) | (inst2 << 12);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[i..], inst);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[(i + 4)..], addr);
          } else {
            var fakeRs1 = inst >> 27;
            if (NotSpecialAuipc(inst, fakeRs1)) {
              i += 4 - 2;
              continue;
            }

            var fakeAddr = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(i + 4)..]);
            var fakeInst2 = (inst >> 12) | (fakeAddr << 20);
            inst = 0x17u | (fakeRs1 << 7) | (fakeAddr & 0xFFFFF000u);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[i..], inst);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[(i + 4)..], fakeInst2);
          }
          i += 8 - 2;
        }
      }
      return i;
    }

    private static int Decode(Span<byte> buffer, uint nowPos) {
      if (buffer.Length < 8)
        return 0;

      var size = buffer.Length - 8;
      var i = 0;
      for (; i <= size; i += 2) {
        uint inst = buffer[i];
        if (inst == 0xEF) {
          uint b1 = buffer[i + 1];
          if ((b1 & 0x0D) != 0)
            continue;

          uint b2 = buffer[i + 2];
          uint b3 = buffer[i + 3];
          var addr = ((b1 & 0xF0u) << 13) | (b2 << 9) | (b3 << 1);
          addr -= nowPos + (uint)i;
          buffer[i + 1] = (byte)((b1 & 0x0F) | ((addr >> 8) & 0xF0));
          buffer[i + 2] = (byte)(((addr >> 16) & 0x0F) | ((addr >> 7) & 0x10) | ((addr << 4) & 0xE0));
          buffer[i + 3] = (byte)(((addr >> 4) & 0x7F) | ((addr >> 13) & 0x80));
          i += 4 - 2;
        } else if ((inst & 0x7F) == 0x17) {
          uint inst2;
          inst |= (uint)buffer[i + 1] << 8 | (uint)buffer[i + 2] << 16 | (uint)buffer[i + 3] << 24;
          if ((inst & 0xE80) != 0) {
            inst2 = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(i + 4)..]);
            if (NotAuipcPair(inst, inst2) != 0) {
              i += 6 - 2;
              continue;
            }

            var addr = (inst & 0xFFFFF000u) + (inst2 >> 20);
            inst = 0x17u | (2u << 7) | (inst2 << 12);
            inst2 = addr;
          } else {
            var inst2Rs1 = inst >> 27;
            if (NotSpecialAuipc(inst, inst2Rs1)) {
              i += 4 - 2;
              continue;
            }

            var addr = BinaryPrimitives.ReadUInt32BigEndian(buffer[(i + 4)..]) - (nowPos + (uint)i);
            inst2 = (inst >> 12) | (addr << 20);
            inst = 0x17u | (inst2Rs1 << 7) | ((addr + 0x800u) & 0xFFFFF000u);
          }

          BinaryPrimitives.WriteUInt32LittleEndian(buffer[i..], inst);
          BinaryPrimitives.WriteUInt32LittleEndian(buffer[(i + 4)..], inst2);
          i += 8 - 2;
        }
      }
      return i;
    }
  }
}

/// <summary>The instruction sets <see cref="BranchConverter"/> knows.</summary>
public enum BranchArchitecture {
  /// <summary>x86 / x86-64 CALL and JMP (7z BCJ, 0x03030103; xz 0x04).</summary>
  X86,
  /// <summary>PowerPC big-endian (7z 0x03030205; xz 0x05).</summary>
  PowerPC,
  /// <summary>IA-64 Itanium (7z 0x03030401; xz 0x06).</summary>
  IA64,
  /// <summary>ARM little-endian (7z 0x03030501; xz 0x07).</summary>
  Arm,
  /// <summary>ARM Thumb (7z 0x03030701; xz 0x08).</summary>
  ArmThumb,
  /// <summary>SPARC (7z 0x03030805; xz 0x09).</summary>
  Sparc,
  /// <summary>ARM64 / AArch64 (7z and xz 0x0A).</summary>
  Arm64,
  /// <summary>RISC-V (7z and xz 0x0B).</summary>
  RiscV,
}
