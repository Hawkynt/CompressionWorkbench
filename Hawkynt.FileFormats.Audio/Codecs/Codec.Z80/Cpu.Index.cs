#pragma warning disable CS1591
namespace Codec.Z80;

/// <summary>
/// The <c>DD</c>/<c>FD</c> index-register opcode pages. They reinterpret the main page so
/// that <c>HL</c> becomes <c>IX</c> (DD) or <c>IY</c> (FD); <c>(HL)</c> accesses become
/// <c>(IX+d)</c>/<c>(IY+d)</c> with a signed displacement byte, and the <c>H</c>/<c>L</c>
/// halves become <c>IXH/IXL</c> (undocumented but used by some players). The <c>CB</c>
/// sub-page becomes <c>DDCB</c>/<c>FDCB</c> where the displacement precedes the opcode.
/// <para>Only the instructions that actually differ when indexed are handled here; any other
/// opcode is dispatched to the main page unchanged (the documented "prefix is ignored"
/// behaviour for instructions that don't reference HL).</para>
/// </summary>
public sealed partial class Cpu {

  private long ExecuteIndex(ref ushort index) {
    var opcode = this.Fetch();
    switch (opcode) {
      // ── 16-bit immediate / memory ops on the index register ──
      case 0x21: index = this.FetchWord(); return 14;                  // LD IX,nn
      case 0x22: { var a = this.FetchWord(); this.WriteMem(a, (byte)index); this.WriteMem((ushort)(a + 1), (byte)(index >> 8)); return 20; }
      case 0x2A: { var a = this.FetchWord(); index = (ushort)(this.ReadMem(a) | (this.ReadMem((ushort)(a + 1)) << 8)); return 20; }
      case 0x23: index++; return 10;                                   // INC IX
      case 0x2B: index--; return 10;                                   // DEC IX
      case 0x36: { var d = (sbyte)this.FetchOperand(); var n = this.FetchOperand(); this.WriteMem((ushort)(index + d), n); return 19; } // LD (IX+d),n
      case 0xE5: this.Push(index); return 15;                          // PUSH IX
      case 0xE1: index = this.Pop(); return 14;                        // POP IX
      case 0xE3: return this.ExSpIndex(ref index);                     // EX (SP),IX
      case 0xE9: this.PC = index; return 8;                            // JP (IX)
      case 0xF9: this.SP = index; return 10;                           // LD SP,IX

      // ── ADD IX,rr ──
      case 0x09: index = this.Add16(index, this.BC); return 15;
      case 0x19: index = this.Add16(index, this.DE); return 15;
      case 0x29: index = this.Add16(index, index); return 15;
      case 0x39: index = this.Add16(index, this.SP); return 15;

      // ── INC/DEC IXH/IXL ──
      case 0x24: index = SetHigh(index, this.Inc8(High(index))); return 8;
      case 0x25: index = SetHigh(index, this.Dec8(High(index))); return 8;
      case 0x2C: index = SetLow(index, this.Inc8(Low(index))); return 8;
      case 0x2D: index = SetLow(index, this.Dec8(Low(index))); return 8;
      case 0x26: index = SetHigh(index, this.FetchOperand()); return 11; // LD IXH,n
      case 0x2E: index = SetLow(index, this.FetchOperand()); return 11;  // LD IXL,n

      // ── INC/DEC/LD (IX+d) ──
      case 0x34: { var a = (ushort)(index + (sbyte)this.FetchOperand()); this.WriteMem(a, this.Inc8(this.ReadMem(a))); return 23; }
      case 0x35: { var a = (ushort)(index + (sbyte)this.FetchOperand()); this.WriteMem(a, this.Dec8(this.ReadMem(a))); return 23; }

      // ── CB sub-page ──
      case 0xCB: return this.ExecuteIndexCb(index);

      default:
        return this.ExecuteIndexGeneric(ref index, opcode);
    }
  }

  // Handles the LD r,r' / LD r,(IX+d) / ALU A,(IX+d) families and the IXH/IXL register forms.
  private long ExecuteIndexGeneric(ref ushort index, byte opcode) {
    // LD r,(IX+d) and LD (IX+d),r  (the 0x40-0x7F block where one operand is (HL)→(IX+d)).
    if (opcode is >= 0x40 and <= 0x7F && opcode != 0x76) {
      var dst = (opcode >> 3) & 0x07;
      var src = opcode & 0x07;
      if (dst == 6) {
        var a = (ushort)(index + (sbyte)this.FetchOperand());
        this.WriteMem(a, this.GetRegIndexed(src, index)); // src is a plain register here
        return 19;
      }
      if (src == 6) {
        var a = (ushort)(index + (sbyte)this.FetchOperand());
        this.SetRegIndexed(dst, this.ReadMem(a), ref index); // dst plain register
        return 19;
      }
      // Neither operand is memory → IXH/IXL substitution for H/L.
      this.SetRegIndexed(dst, this.GetRegIndexed(src, index), ref index);
      return 8;
    }

    // ALU A,(IX+d) and ALU A,IXH/IXL.
    if (opcode is >= 0x80 and <= 0xBF) {
      var op = (opcode >> 3) & 0x07;
      var src = opcode & 0x07;
      byte value;
      long cycles;
      if (src == 6) {
        var a = (ushort)(index + (sbyte)this.FetchOperand());
        value = this.ReadMem(a);
        cycles = 19;
      } else {
        value = this.GetRegIndexed(src, index);
        cycles = 8;
      }
      this.AluOp(op, value);
      return cycles;
    }

    // Anything else: the prefix has no effect, run it on the main page.
    return this.Execute(opcode);
  }

  // Register read with H/L mapped to IXH/IXL (memory index 6 is handled by callers).
  private byte GetRegIndexed(int regIndex, ushort index) => regIndex switch {
    4 => High(index),
    5 => Low(index),
    _ => this.GetReg(regIndex),
  };

  private void SetRegIndexed(int regIndex, byte value, ref ushort index) {
    switch (regIndex) {
      case 4: index = SetHigh(index, value); break;
      case 5: index = SetLow(index, value); break;
      default: this.SetReg(regIndex, value); break;
    }
  }

  private long ExSpIndex(ref ushort index) {
    var lo = this.ReadMem(this.SP);
    var hi = this.ReadMem((ushort)(this.SP + 1));
    this.WriteMem(this.SP, (byte)index);
    this.WriteMem((ushort)(this.SP + 1), (byte)(index >> 8));
    index = (ushort)(lo | (hi << 8));
    return 23;
  }

  // DDCB/FDCB: displacement byte, then the CB opcode; the operation acts on (IX+d) and, for
  // the non-BIT forms, the result is ALSO copied back into the encoded register (undocumented).
  private long ExecuteIndexCb(ushort index) {
    var d = (sbyte)this.FetchOperand();
    var opcode = this.FetchOperand(); // the CB opcode does not bump R again
    var addr = (ushort)(index + d);
    var value = this.ReadMem(addr);
    var reg = opcode & 0x07;
    var bit = (opcode >> 3) & 0x07;

    if (opcode < 0x40) {
      var op = (opcode >> 3) & 0x1F;
      var result = this.RotateShift(op, value);
      this.WriteMem(addr, result);
      if (reg != 6) this.SetReg(reg, result); // undocumented register copy
      return 23;
    }

    if (opcode < 0x80) {
      this.Bit(bit, value);
      return 20;
    }

    if (opcode < 0xC0) {
      var result = (byte)(value & ~(1 << bit));
      this.WriteMem(addr, result);
      if (reg != 6) this.SetReg(reg, result);
      return 23;
    }

    var setResult = (byte)(value | (1 << bit));
    this.WriteMem(addr, setResult);
    if (reg != 6) this.SetReg(reg, setResult);
    return 23;
  }

  private static byte High(ushort value) => (byte)(value >> 8);
  private static byte Low(ushort value) => (byte)value;
  private static ushort SetHigh(ushort value, byte hi) => (ushort)((hi << 8) | (value & 0x00FF));
  private static ushort SetLow(ushort value, byte lo) => (ushort)((value & 0xFF00) | lo);
}
