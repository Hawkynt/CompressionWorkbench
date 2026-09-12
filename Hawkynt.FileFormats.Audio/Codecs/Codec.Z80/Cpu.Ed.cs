#pragma warning disable CS1591
namespace Codec.Z80;

/// <summary>
/// The <c>ED</c> opcode page: 16-bit <c>ADC/SBC HL</c>, <c>LD (nn),rr</c>/<c>LD rr,(nn)</c>,
/// <c>NEG</c>, <c>RETN/RETI</c>, the interrupt-mode selects, the <c>I</c>/<c>R</c> register
/// transfers, <c>RRD/RLD</c>, the I/O group (<c>IN/OUT (C)</c>) and the block
/// transfer/search/I-O instructions (<c>LDI/LDIR/CPI/CPIR/INI/INIR/OUTI/OTIR</c> and the
/// decrementing variants).
/// </summary>
public sealed partial class Cpu {

  private long ExecuteEd() {
    var opcode = this.Fetch();
    switch (opcode) {
      // ── 16-bit SBC/ADC HL,rr ──
      case 0x42: this.Sbc16(this.BC); return 15;
      case 0x52: this.Sbc16(this.DE); return 15;
      case 0x62: this.Sbc16(this.HL); return 15;
      case 0x72: this.Sbc16(this.SP); return 15;
      case 0x4A: this.Adc16(this.BC); return 15;
      case 0x5A: this.Adc16(this.DE); return 15;
      case 0x6A: this.Adc16(this.HL); return 15;
      case 0x7A: this.Adc16(this.SP); return 15;

      // ── LD (nn),rr / LD rr,(nn) ──
      case 0x43: this.StoreWord(this.BC); return 20;
      case 0x53: this.StoreWord(this.DE); return 20;
      case 0x63: this.StoreWord(this.HL); return 20;
      case 0x73: this.StoreWord(this.SP); return 20;
      case 0x4B: this.BC = this.LoadWord(); return 20;
      case 0x5B: this.DE = this.LoadWord(); return 20;
      case 0x6B: this.HL = this.LoadWord(); return 20;
      case 0x7B: this.SP = this.LoadWord(); return 20;

      // ── NEG (the 0x44 primary plus its undocumented mirrors) ──
      case 0x44: case 0x4C: case 0x54: case 0x5C:
      case 0x64: case 0x6C: case 0x74: case 0x7C:
        this.Neg(); return 8;

      // ── RETN / RETI ──
      case 0x45: case 0x55: case 0x65: case 0x75: // RETN mirrors
        this.IFF1 = this.IFF2; this.PC = this.Pop(); return 14;
      case 0x4D: case 0x5D: case 0x6D: case 0x7D: // RETI mirrors
        this.IFF1 = this.IFF2; this.PC = this.Pop(); return 14;

      // ── interrupt mode ──
      case 0x46: case 0x4E: case 0x66: case 0x6E: this.InterruptMode = 0; return 8;
      case 0x56: case 0x76: this.InterruptMode = 1; return 8;
      case 0x5E: case 0x7E: this.InterruptMode = 2; return 8;

      // ── I/R register transfers ──
      case 0x47: this.I = this.A; return 9; // LD I,A
      case 0x4F: this.R = this.A; return 9; // LD R,A
      case 0x57: this.LdaIr(this.I); return 9; // LD A,I
      case 0x5F: this.LdaIr(this.R); return 9; // LD A,R

      // ── RRD / RLD ──
      case 0x67: this.Rrd(); return 18;
      case 0x6F: this.Rld(); return 18;

      // ── IN r,(C) / OUT (C),r ──
      case 0x40: this.B = this.InC(); return 12;
      case 0x48: this.C = this.InC(); return 12;
      case 0x50: this.D = this.InC(); return 12;
      case 0x58: this.E = this.InC(); return 12;
      case 0x60: this.H = this.InC(); return 12;
      case 0x68: this.L = this.InC(); return 12;
      case 0x70: this.InC(); return 12;        // IN (C) — flags only
      case 0x78: this.A = this.InC(); return 12;
      case 0x41: this.WriteIo(this.BC, this.B); return 12;
      case 0x49: this.WriteIo(this.BC, this.C); return 12;
      case 0x51: this.WriteIo(this.BC, this.D); return 12;
      case 0x59: this.WriteIo(this.BC, this.E); return 12;
      case 0x61: this.WriteIo(this.BC, this.H); return 12;
      case 0x69: this.WriteIo(this.BC, this.L); return 12;
      case 0x71: this.WriteIo(this.BC, 0); return 12;        // OUT (C),0
      case 0x79: this.WriteIo(this.BC, this.A); return 12;

      // ── block transfer/search/I-O ──
      case 0xA0: return this.Ldi(increment: true, repeat: false);
      case 0xB0: return this.Ldi(increment: true, repeat: true);
      case 0xA8: return this.Ldi(increment: false, repeat: false);
      case 0xB8: return this.Ldi(increment: false, repeat: true);
      case 0xA1: return this.Cpi(increment: true, repeat: false);
      case 0xB1: return this.Cpi(increment: true, repeat: true);
      case 0xA9: return this.Cpi(increment: false, repeat: false);
      case 0xB9: return this.Cpi(increment: false, repeat: true);
      case 0xA2: return this.Ini(increment: true, repeat: false);
      case 0xB2: return this.Ini(increment: true, repeat: true);
      case 0xAA: return this.Ini(increment: false, repeat: false);
      case 0xBA: return this.Ini(increment: false, repeat: true);
      case 0xA3: return this.Outi(increment: true, repeat: false);
      case 0xB3: return this.Outi(increment: true, repeat: true);
      case 0xAB: return this.Outi(increment: false, repeat: false);
      case 0xBB: return this.Outi(increment: false, repeat: true);

      default:
        return 8; // undefined ED opcode behaves as a NOP (documented best-effort)
    }
  }

  private void StoreWord(ushort value) {
    var addr = this.FetchWord();
    this.WriteMem(addr, (byte)value);
    this.WriteMem((ushort)(addr + 1), (byte)(value >> 8));
  }

  private ushort LoadWord() {
    var addr = this.FetchWord();
    var lo = this.ReadMem(addr);
    var hi = this.ReadMem((ushort)(addr + 1));
    return (ushort)(lo | (hi << 8));
  }

  private void LdaIr(byte source) {
    this.A = source;
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.PV, this.IFF2);
    this.SetSzyx(source);
  }

  private byte InC() {
    var value = this.ReadIo(this.BC);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.PV, Parity(value));
    this.SetSzyx(value);
    return value;
  }

  private void Rrd() {
    var memory = this.ReadMem(this.HL);
    var newMem = (byte)((memory >> 4) | (this.A << 4));
    this.A = (byte)((this.A & 0xF0) | (memory & 0x0F));
    this.WriteMem(this.HL, newMem);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.PV, Parity(this.A));
    this.SetSzyx(this.A);
  }

  private void Rld() {
    var memory = this.ReadMem(this.HL);
    var newMem = (byte)((memory << 4) | (this.A & 0x0F));
    this.A = (byte)((this.A & 0xF0) | (memory >> 4));
    this.WriteMem(this.HL, newMem);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.PV, Parity(this.A));
    this.SetSzyx(this.A);
  }

  // ── block ops ───────────────────────────────────────────────────────────────
  private long Ldi(bool increment, bool repeat) {
    var value = this.ReadMem(this.HL);
    this.WriteMem(this.DE, value);
    if (increment) { this.HL++; this.DE++; } else { this.HL--; this.DE--; }
    this.BC--;
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.PV, this.BC != 0);
    // Y/X follow (A + transferred value): bit 1 → Y, bit 3 → X (documented quirk).
    var n = (byte)(this.A + value);
    this.SetFlag(Flags.Y, (n & 0x02) != 0);
    this.SetFlag(Flags.X, (n & 0x08) != 0);
    if (repeat && this.BC != 0) {
      this.PC -= 2; // re-execute the ED-prefixed opcode
      return 21;
    }
    return 16;
  }

  private long Cpi(bool increment, bool repeat) {
    var value = this.ReadMem(this.HL);
    var result = (byte)(this.A - value);
    var half = (this.A & 0x0F) - (value & 0x0F);
    if (increment) this.HL++; else this.HL--;
    this.BC--;
    this.SetFlag(Flags.N, true);
    this.SetFlag(Flags.H, (half & 0x10) != 0);
    this.SetFlag(Flags.PV, this.BC != 0);
    this.SetFlag(Flags.S, (result & 0x80) != 0);
    this.SetFlag(Flags.Z, result == 0);
    var n = (byte)(result - (this.HasFlag(Flags.H) ? 1 : 0));
    this.SetFlag(Flags.Y, (n & 0x02) != 0);
    this.SetFlag(Flags.X, (n & 0x08) != 0);
    if (repeat && this.BC != 0 && result != 0) {
      this.PC -= 2;
      return 21;
    }
    return 16;
  }

  private long Ini(bool increment, bool repeat) {
    var value = this.ReadIo(this.BC);
    this.WriteMem(this.HL, value);
    this.B--;
    if (increment) this.HL++; else this.HL--;
    this.SetFlag(Flags.N, (value & 0x80) != 0);
    this.SetFlag(Flags.Z, this.B == 0);
    this.SetFlag(Flags.S, (this.B & 0x80) != 0);
    this.SetFlag(Flags.Y, (this.B & 0x20) != 0);
    this.SetFlag(Flags.X, (this.B & 0x08) != 0);
    // H/PV per the documented (k = value + ((C±1)&0xFF)) rule.
    var k = value + ((this.C + (increment ? 1 : -1)) & 0xFF);
    this.SetFlag(Flags.H, k > 0xFF);
    this.SetFlag(Flags.C, k > 0xFF);
    this.SetFlag(Flags.PV, Parity((byte)((k & 0x07) ^ this.B)));
    if (repeat && this.B != 0) {
      this.PC -= 2;
      return 21;
    }
    return 16;
  }

  private long Outi(bool increment, bool repeat) {
    var value = this.ReadMem(this.HL);
    this.B--;
    this.WriteIo(this.BC, value);
    if (increment) this.HL++; else this.HL--;
    this.SetFlag(Flags.N, (value & 0x80) != 0);
    this.SetFlag(Flags.Z, this.B == 0);
    this.SetFlag(Flags.S, (this.B & 0x80) != 0);
    this.SetFlag(Flags.Y, (this.B & 0x20) != 0);
    this.SetFlag(Flags.X, (this.B & 0x08) != 0);
    var k = value + this.L;
    this.SetFlag(Flags.H, k > 0xFF);
    this.SetFlag(Flags.C, k > 0xFF);
    this.SetFlag(Flags.PV, Parity((byte)((k & 0x07) ^ this.B)));
    if (repeat && this.B != 0) {
      this.PC -= 2;
      return 21;
    }
    return 16;
  }
}
