#pragma warning disable CS1591
namespace Codec.Z80;

/// <summary>
/// Main (un-prefixed) opcode dispatch plus the public stepping/run helpers.
/// </summary>
public sealed partial class Cpu {

  /// <summary>
  /// Executes one full instruction (including any prefix bytes) and returns the number of
  /// T-states it consumed. A pending maskable interrupt is not auto-serviced here; use
  /// <see cref="RaiseIrq"/> at frame boundaries.
  /// </summary>
  public long Step() {
    if (this.Halted) {
      // HALT executes NOPs until an interrupt; advance R and burn 4 T-states.
      this.Fetch();
      this.PC--; // stay on the HALT instruction
      return 4;
    }

    var opcode = this.Fetch();
    return this.Execute(opcode);
  }

  // ── 8-bit register file indexing (B,C,D,E,H,L,(HL),A) ───────────────────────
  // index: 0=B 1=C 2=D 3=E 4=H 5=L 6=(HL) 7=A
  private byte GetReg(int index) => index switch {
    0 => this.B, 1 => this.C, 2 => this.D, 3 => this.E,
    4 => this.H, 5 => this.L, 6 => this.ReadMem(this.HL), _ => this.A,
  };

  private void SetReg(int index, byte value) {
    switch (index) {
      case 0: this.B = value; break;
      case 1: this.C = value; break;
      case 2: this.D = value; break;
      case 3: this.E = value; break;
      case 4: this.H = value; break;
      case 5: this.L = value; break;
      case 6: this.WriteMem(this.HL, value); break;
      default: this.A = value; break;
    }
  }

  private long Execute(byte opcode) {
    switch (opcode) {
      case 0x00: return 4; // NOP

      // ── prefixes ──
      case 0xCB: return this.ExecuteCb();
      case 0xED: return this.ExecuteEd();
      case 0xDD: return this.ExecuteIndex(ref this.IX);
      case 0xFD: return this.ExecuteIndex(ref this.IY);

      // ── 16-bit immediate loads ──
      case 0x01: this.BC = this.FetchWord(); return 10;
      case 0x11: this.DE = this.FetchWord(); return 10;
      case 0x21: this.HL = this.FetchWord(); return 10;
      case 0x31: this.SP = this.FetchWord(); return 10;

      // ── LD (nn),HL / LD HL,(nn) / LD (nn),A / LD A,(nn) ──
      case 0x22: { var a = this.FetchWord(); this.WriteMem(a, this.L); this.WriteMem((ushort)(a + 1), this.H); return 16; }
      case 0x2A: { var a = this.FetchWord(); this.L = this.ReadMem(a); this.H = this.ReadMem((ushort)(a + 1)); return 16; }
      case 0x32: { var a = this.FetchWord(); this.WriteMem(a, this.A); return 13; }
      case 0x3A: { var a = this.FetchWord(); this.A = this.ReadMem(a); return 13; }

      // ── LD (BC/DE),A and LD A,(BC/DE) ──
      case 0x02: this.WriteMem(this.BC, this.A); return 7;
      case 0x12: this.WriteMem(this.DE, this.A); return 7;
      case 0x0A: this.A = this.ReadMem(this.BC); return 7;
      case 0x1A: this.A = this.ReadMem(this.DE); return 7;

      // ── INC/DEC 16-bit ──
      case 0x03: this.BC++; return 6;
      case 0x13: this.DE++; return 6;
      case 0x23: this.HL++; return 6;
      case 0x33: this.SP++; return 6;
      case 0x0B: this.BC--; return 6;
      case 0x1B: this.DE--; return 6;
      case 0x2B: this.HL--; return 6;
      case 0x3B: this.SP--; return 6;

      // ── ADD HL,rr ──
      case 0x09: this.HL = this.Add16(this.HL, this.BC); return 11;
      case 0x19: this.HL = this.Add16(this.HL, this.DE); return 11;
      case 0x29: this.HL = this.Add16(this.HL, this.HL); return 11;
      case 0x39: this.HL = this.Add16(this.HL, this.SP); return 11;

      // ── INC/DEC 8-bit ──
      case 0x04: this.B = this.Inc8(this.B); return 4;
      case 0x0C: this.C = this.Inc8(this.C); return 4;
      case 0x14: this.D = this.Inc8(this.D); return 4;
      case 0x1C: this.E = this.Inc8(this.E); return 4;
      case 0x24: this.H = this.Inc8(this.H); return 4;
      case 0x2C: this.L = this.Inc8(this.L); return 4;
      case 0x34: { var a = this.HL; this.WriteMem(a, this.Inc8(this.ReadMem(a))); return 11; }
      case 0x3C: this.A = this.Inc8(this.A); return 4;
      case 0x05: this.B = this.Dec8(this.B); return 4;
      case 0x0D: this.C = this.Dec8(this.C); return 4;
      case 0x15: this.D = this.Dec8(this.D); return 4;
      case 0x1D: this.E = this.Dec8(this.E); return 4;
      case 0x25: this.H = this.Dec8(this.H); return 4;
      case 0x2D: this.L = this.Dec8(this.L); return 4;
      case 0x35: { var a = this.HL; this.WriteMem(a, this.Dec8(this.ReadMem(a))); return 11; }
      case 0x3D: this.A = this.Dec8(this.A); return 4;

      // ── LD r,n ──
      case 0x06: this.B = this.FetchOperand(); return 7;
      case 0x0E: this.C = this.FetchOperand(); return 7;
      case 0x16: this.D = this.FetchOperand(); return 7;
      case 0x1E: this.E = this.FetchOperand(); return 7;
      case 0x26: this.H = this.FetchOperand(); return 7;
      case 0x2E: this.L = this.FetchOperand(); return 7;
      case 0x36: this.WriteMem(this.HL, this.FetchOperand()); return 10;
      case 0x3E: this.A = this.FetchOperand(); return 7;

      // ── rotates on A ──
      case 0x07: this.Rlca(); return 4;
      case 0x0F: this.Rrca(); return 4;
      case 0x17: this.Rla(); return 4;
      case 0x1F: this.Rra(); return 4;
      case 0x27: this.Daa(); return 4;
      case 0x2F: this.Cpl(); return 4;
      case 0x37: this.Scf(); return 4;
      case 0x3F: this.Ccf(); return 4;

      // ── relative jumps ──
      case 0x18: return this.Jr(true);
      case 0x20: return this.Jr(!this.HasFlag(Flags.Z));
      case 0x28: return this.Jr(this.HasFlag(Flags.Z));
      case 0x30: return this.Jr(!this.HasFlag(Flags.C));
      case 0x38: return this.Jr(this.HasFlag(Flags.C));
      case 0x10: return this.Djnz();

      // ── EX AF,AF' / EXX / EX DE,HL / EX (SP),HL ──
      case 0x08: this.ExAf(); return 4;
      case 0xD9: this.Exx(); return 4;
      case 0xEB: (this.D, this.H) = (this.H, this.D); (this.E, this.L) = (this.L, this.E); return 4;
      case 0xE3: return this.ExSpHl();

      // ── HALT ──
      case 0x76: this.Halted = true; return 4;

      // ── ALU A,r (0x80-0xBF) ──
      case >= 0x80 and <= 0xBF: {
        var op = (opcode >> 3) & 0x07;
        var src = opcode & 0x07;
        var value = this.GetReg(src);
        this.AluOp(op, value);
        return src == 6 ? 7 : 4;
      }

      // ── ALU A,n ──
      case 0xC6: this.AluOp(0, this.FetchOperand()); return 7;
      case 0xCE: this.AluOp(1, this.FetchOperand()); return 7;
      case 0xD6: this.AluOp(2, this.FetchOperand()); return 7;
      case 0xDE: this.AluOp(3, this.FetchOperand()); return 7;
      case 0xE6: this.AluOp(4, this.FetchOperand()); return 7;
      case 0xEE: this.AluOp(5, this.FetchOperand()); return 7;
      case 0xF6: this.AluOp(6, this.FetchOperand()); return 7;
      case 0xFE: this.AluOp(7, this.FetchOperand()); return 7;

      // ── PUSH/POP ──
      case 0xC5: this.Push(this.BC); return 11;
      case 0xD5: this.Push(this.DE); return 11;
      case 0xE5: this.Push(this.HL); return 11;
      case 0xF5: this.Push(this.AF); return 11;
      case 0xC1: this.BC = this.Pop(); return 10;
      case 0xD1: this.DE = this.Pop(); return 10;
      case 0xE1: this.HL = this.Pop(); return 10;
      case 0xF1: this.AF = this.Pop(); return 10;

      // ── conditional + unconditional RET ──
      case 0xC9: this.PC = this.Pop(); return 10;
      case 0xC0: return this.RetIf(!this.HasFlag(Flags.Z));
      case 0xC8: return this.RetIf(this.HasFlag(Flags.Z));
      case 0xD0: return this.RetIf(!this.HasFlag(Flags.C));
      case 0xD8: return this.RetIf(this.HasFlag(Flags.C));
      case 0xE0: return this.RetIf(!this.HasFlag(Flags.PV));
      case 0xE8: return this.RetIf(this.HasFlag(Flags.PV));
      case 0xF0: return this.RetIf(!this.HasFlag(Flags.S));
      case 0xF8: return this.RetIf(this.HasFlag(Flags.S));

      // ── JP ──
      case 0xC3: this.PC = this.FetchWord(); return 10;
      case 0xC2: return this.JpIf(!this.HasFlag(Flags.Z));
      case 0xCA: return this.JpIf(this.HasFlag(Flags.Z));
      case 0xD2: return this.JpIf(!this.HasFlag(Flags.C));
      case 0xDA: return this.JpIf(this.HasFlag(Flags.C));
      case 0xE2: return this.JpIf(!this.HasFlag(Flags.PV));
      case 0xEA: return this.JpIf(this.HasFlag(Flags.PV));
      case 0xF2: return this.JpIf(!this.HasFlag(Flags.S));
      case 0xFA: return this.JpIf(this.HasFlag(Flags.S));
      case 0xE9: this.PC = this.HL; return 4; // JP (HL)

      // ── CALL ──
      case 0xCD: { var a = this.FetchWord(); this.Push(this.PC); this.PC = a; return 17; }
      case 0xC4: return this.CallIf(!this.HasFlag(Flags.Z));
      case 0xCC: return this.CallIf(this.HasFlag(Flags.Z));
      case 0xD4: return this.CallIf(!this.HasFlag(Flags.C));
      case 0xDC: return this.CallIf(this.HasFlag(Flags.C));
      case 0xE4: return this.CallIf(!this.HasFlag(Flags.PV));
      case 0xEC: return this.CallIf(this.HasFlag(Flags.PV));
      case 0xF4: return this.CallIf(!this.HasFlag(Flags.S));
      case 0xFC: return this.CallIf(this.HasFlag(Flags.S));

      // ── RST ──
      case 0xC7: return this.Rst(0x00);
      case 0xCF: return this.Rst(0x08);
      case 0xD7: return this.Rst(0x10);
      case 0xDF: return this.Rst(0x18);
      case 0xE7: return this.Rst(0x20);
      case 0xEF: return this.Rst(0x28);
      case 0xF7: return this.Rst(0x30);
      case 0xFF: return this.Rst(0x38);

      // ── I/O ──
      case 0xD3: { var n = this.FetchOperand(); this.WriteIo((ushort)((this.A << 8) | n), this.A); return 11; } // OUT (n),A
      case 0xDB: { var n = this.FetchOperand(); this.A = this.ReadIo((ushort)((this.A << 8) | n)); return 11; }  // IN A,(n)

      // ── SP/HL, interrupts ──
      case 0xF9: this.SP = this.HL; return 6; // LD SP,HL
      case 0xF3: this.IFF1 = this.IFF2 = false; return 4; // DI
      case 0xFB: this.IFF1 = this.IFF2 = true; return 4;  // EI

      // ── LD r,r' (0x40-0x7F minus HALT, handled above) — also the catch-all ──
      default: {
        var dst = (opcode >> 3) & 0x07;
        var src = opcode & 0x07;
        this.SetReg(dst, this.GetReg(src));
        return (dst == 6 || src == 6) ? 7 : 4;
      }
    }
  }

  // op: 0=ADD 1=ADC 2=SUB 3=SBC 4=AND 5=XOR 6=OR 7=CP
  private void AluOp(int op, byte value) {
    switch (op) {
      case 0: this.Add8(value, withCarry: false); break;
      case 1: this.Add8(value, withCarry: true); break;
      case 2: this.Sub8(value, withCarry: false, store: true); break;
      case 3: this.Sub8(value, withCarry: true, store: true); break;
      case 4: this.And8(value); break;
      case 5: this.Xor8(value); break;
      case 6: this.Or8(value); break;
      default: this.Sub8(value, withCarry: false, store: false); break; // CP
    }
  }

  // ── flow helpers ────────────────────────────────────────────────────────────
  private long Jr(bool condition) {
    var offset = (sbyte)this.FetchOperand();
    if (!condition)
      return 7;
    this.PC = (ushort)(this.PC + offset);
    return 12;
  }

  private long Djnz() {
    var offset = (sbyte)this.FetchOperand();
    this.B--;
    if (this.B == 0)
      return 8;
    this.PC = (ushort)(this.PC + offset);
    return 13;
  }

  private long JpIf(bool condition) {
    var addr = this.FetchWord();
    if (condition) this.PC = addr;
    return 10;
  }

  private long CallIf(bool condition) {
    var addr = this.FetchWord();
    if (!condition)
      return 10;
    this.Push(this.PC);
    this.PC = addr;
    return 17;
  }

  private long RetIf(bool condition) {
    if (!condition)
      return 5;
    this.PC = this.Pop();
    return 11;
  }

  private long Rst(ushort target) {
    this.Push(this.PC);
    this.PC = target;
    return 11;
  }

  private void ExAf() {
    (this.A, this.A2) = (this.A2, this.A);
    (this.F, this.F2) = (this.F2, this.F);
  }

  private void Exx() {
    (this.B, this.B2) = (this.B2, this.B);
    (this.C, this.C2) = (this.C2, this.C);
    (this.D, this.D2) = (this.D2, this.D);
    (this.E, this.E2) = (this.E2, this.E);
    (this.H, this.H2) = (this.H2, this.H);
    (this.L, this.L2) = (this.L2, this.L);
  }

  private long ExSpHl() {
    var lo = this.ReadMem(this.SP);
    var hi = this.ReadMem((ushort)(this.SP + 1));
    this.WriteMem(this.SP, this.L);
    this.WriteMem((ushort)(this.SP + 1), this.H);
    this.L = lo; this.H = hi;
    return 19;
  }
}
