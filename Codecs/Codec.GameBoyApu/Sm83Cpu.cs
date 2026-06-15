#pragma warning disable CS1591
namespace Codec.GameBoyApu;

/// <summary>
/// A cycle-counting Sharp SM83 (LR35902) CPU core — the Game Boy's processor. It is an
/// 8080/Z80-derived design with its own instruction set: it has no IX/IY, no shadow
/// registers, and a reduced/reshuffled opcode map (notably <c>$08 LD (a16),SP</c>,
/// <c>$E0/$F0 LDH</c>, <c>$E2/$F2 LD (C),A</c>, <c>$E8 ADD SP,r8</c>, <c>$F8 LD HL,SP+r8</c>,
/// and <c>$22/$32/$2A/$3A</c> HL-increment/decrement loads). The full official opcode set is
/// implemented including the <c>CB</c>-prefixed rotate/shift/bit/set/res group and the
/// decimal-adjust <c>DAA</c>.
/// <para>Cycle counts are returned in T-states (machine clocks at 4.194304 MHz), matching the
/// canonical Game Boy timing tables.</para>
/// <para><c>HALT</c> is modelled as a NOP that consumes 4 cycles: a music player's init/play
/// routines either never execute it or expect it to simply pause until the next play tick, and
/// since this core is driven a play-call at a time there is no interrupt source to wait for.
/// <c>STOP</c> is likewise a 4-cycle NOP. <c>EI</c>/<c>DI</c> toggle the interrupt-master flag
/// (tracked but otherwise inert, as no interrupts are delivered during a player's subroutine
/// call). The eight illegal opcodes (<c>$D3 $DB $DD $E3 $E4 $EB $EC $ED $F4 $FC $FD</c>) are
/// decoded as 4-cycle no-ops.</para>
/// Memory is accessed exclusively through <see cref="ISm83Bus"/>.
/// </summary>
public sealed class Sm83Cpu {

  /// <summary>Flag bits in the F register (low nibble is always zero on SM83).</summary>
  [Flags]
  public enum Flags : byte {
    Carry = 0x10,
    HalfCarry = 0x20,
    Subtract = 0x40,
    Zero = 0x80,
  }

  private readonly ISm83Bus _bus;

  public byte A;
  public byte F;
  public byte B;
  public byte C;
  public byte D;
  public byte E;
  public byte H;
  public byte L;
  public ushort SP;
  public ushort PC;

  /// <summary>Interrupt-master-enable latch. Tracked for fidelity; no interrupts are delivered.</summary>
  public bool Ime;

  public Sm83Cpu(ISm83Bus bus) => this._bus = bus;

  // ── 16-bit register pairs ─────────────────────────────────────────────────────

  public ushort BC {
    get => (ushort)((this.B << 8) | this.C);
    set { this.B = (byte)(value >> 8); this.C = (byte)value; }
  }

  public ushort DE {
    get => (ushort)((this.D << 8) | this.E);
    set { this.D = (byte)(value >> 8); this.E = (byte)value; }
  }

  public ushort HL {
    get => (ushort)((this.H << 8) | this.L);
    set { this.H = (byte)(value >> 8); this.L = (byte)value; }
  }

  public ushort AF {
    get => (ushort)((this.A << 8) | (this.F & 0xF0));
    set { this.A = (byte)(value >> 8); this.F = (byte)(value & 0xF0); }
  }

  // ── bus helpers ───────────────────────────────────────────────────────────────

  private byte Read(ushort addr) => this._bus.Read(addr);
  private void Write(ushort addr, byte value) => this._bus.Write(addr, value);

  private byte Fetch() => this.Read(this.PC++);

  private ushort FetchWord() {
    var lo = this.Fetch();
    var hi = this.Fetch();
    return (ushort)(lo | (hi << 8));
  }

  private void Push(ushort value) {
    this.Write((ushort)(--this.SP), (byte)(value >> 8));
    this.Write((ushort)(--this.SP), (byte)value);
  }

  private ushort Pop() {
    var lo = this.Read(this.SP++);
    var hi = this.Read(this.SP++);
    return (ushort)(lo | (hi << 8));
  }

  // ── flag helpers ──────────────────────────────────────────────────────────────

  private bool HasFlag(Flags flag) => (this.F & (byte)flag) != 0;

  private void SetFlag(Flags flag, bool on) {
    if (on) this.F |= (byte)flag; else this.F = (byte)(this.F & ~(byte)flag);
    this.F &= 0xF0;
  }

  private void SetFlags(bool z, bool n, bool h, bool c) {
    this.F = (byte)((z ? 0x80 : 0) | (n ? 0x40 : 0) | (h ? 0x20 : 0) | (c ? 0x10 : 0));
  }

  /// <summary>
  /// Calls into a subroutine at <paramref name="address"/> using the player convention:
  /// a sentinel return address is pushed so the matching <c>RET</c> lands on a known PC,
  /// at which point execution stops. Returns the cycles consumed (capped). The GBS player
  /// uses this to invoke the tune's init and play routines.
  /// </summary>
  public long RunUntilRet(ushort address, long maxCycles) {
    // Push a sentinel return address; the routine's RET pulls it and lands on the sentinel
    // PC, where we stop. The stack must also have returned to (or above) where it was so a
    // nested RET inside the routine does not trip the check early.
    const ushort sentinel = 0x0001;
    var targetSp = this.SP;
    this.Push(sentinel);
    this.PC = address;

    long cycles = 0;
    while (cycles < maxCycles) {
      if (this.PC == sentinel && this.SP == targetSp)
        break;
      cycles += this.Step();
    }
    return cycles;
  }

  // ── 8-bit ALU primitives ──────────────────────────────────────────────────────

  private void AddA(byte value) {
    var sum = this.A + value;
    var half = (this.A & 0x0F) + (value & 0x0F);
    var result = (byte)sum;
    this.SetFlags(result == 0, false, half > 0x0F, sum > 0xFF);
    this.A = result;
  }

  private void AdcA(byte value) {
    var carry = this.HasFlag(Flags.Carry) ? 1 : 0;
    var sum = this.A + value + carry;
    var half = (this.A & 0x0F) + (value & 0x0F) + carry;
    var result = (byte)sum;
    this.SetFlags(result == 0, false, half > 0x0F, sum > 0xFF);
    this.A = result;
  }

  private void SubA(byte value) {
    var diff = this.A - value;
    var half = (this.A & 0x0F) - (value & 0x0F);
    var result = (byte)diff;
    this.SetFlags(result == 0, true, half < 0, diff < 0);
    this.A = result;
  }

  private void SbcA(byte value) {
    var carry = this.HasFlag(Flags.Carry) ? 1 : 0;
    var diff = this.A - value - carry;
    var half = (this.A & 0x0F) - (value & 0x0F) - carry;
    var result = (byte)diff;
    this.SetFlags(result == 0, true, half < 0, diff < 0);
    this.A = result;
  }

  private void AndA(byte value) {
    this.A &= value;
    this.SetFlags(this.A == 0, false, true, false);
  }

  private void XorA(byte value) {
    this.A ^= value;
    this.SetFlags(this.A == 0, false, false, false);
  }

  private void OrA(byte value) {
    this.A |= value;
    this.SetFlags(this.A == 0, false, false, false);
  }

  private void CpA(byte value) {
    var diff = this.A - value;
    var half = (this.A & 0x0F) - (value & 0x0F);
    this.SetFlags((byte)diff == 0, true, half < 0, diff < 0);
  }

  private byte Inc8(byte value) {
    var result = (byte)(value + 1);
    this.SetFlag(Flags.Zero, result == 0);
    this.SetFlag(Flags.Subtract, false);
    this.SetFlag(Flags.HalfCarry, (value & 0x0F) == 0x0F);
    return result;
  }

  private byte Dec8(byte value) {
    var result = (byte)(value - 1);
    this.SetFlag(Flags.Zero, result == 0);
    this.SetFlag(Flags.Subtract, true);
    this.SetFlag(Flags.HalfCarry, (value & 0x0F) == 0x00);
    return result;
  }

  private void AddHl(ushort value) {
    var hl = this.HL;
    var sum = hl + value;
    var half = (hl & 0x0FFF) + (value & 0x0FFF);
    this.SetFlag(Flags.Subtract, false);
    this.SetFlag(Flags.HalfCarry, half > 0x0FFF);
    this.SetFlag(Flags.Carry, sum > 0xFFFF);
    this.HL = (ushort)sum;
  }

  // ADD SP,r8 / LD HL,SP+r8 share this carry computation (flags come from the low byte add).
  private ushort AddSpSigned(sbyte offset) {
    var sp = this.SP;
    var result = (ushort)(sp + offset);
    var half = (sp & 0x0F) + (offset & 0x0F);
    var carry = (sp & 0xFF) + (offset & 0xFF);
    this.SetFlags(false, false, half > 0x0F, carry > 0xFF);
    return result;
  }

  private void Daa() {
    int a = this.A;
    if (!this.HasFlag(Flags.Subtract)) {
      if (this.HasFlag(Flags.HalfCarry) || (a & 0x0F) > 0x09) a += 0x06;
      if (this.HasFlag(Flags.Carry) || a > 0x9F) { a += 0x60; this.SetFlag(Flags.Carry, true); }
    } else {
      if (this.HasFlag(Flags.HalfCarry)) a = (a - 0x06) & 0xFF;
      if (this.HasFlag(Flags.Carry)) a -= 0x60;
    }
    this.A = (byte)a;
    this.SetFlag(Flags.Zero, this.A == 0);
    this.SetFlag(Flags.HalfCarry, false);
  }

  // ── rotates (the A-register variants always clear Z; the CB variants set Z) ──────

  private byte Rlc(byte value, bool affectZero) {
    var carry = (value & 0x80) != 0;
    var result = (byte)((value << 1) | (carry ? 1 : 0));
    this.SetFlags(affectZero && result == 0, false, false, carry);
    return result;
  }

  private byte Rrc(byte value, bool affectZero) {
    var carry = (value & 0x01) != 0;
    var result = (byte)((value >> 1) | (carry ? 0x80 : 0));
    this.SetFlags(affectZero && result == 0, false, false, carry);
    return result;
  }

  private byte Rl(byte value, bool affectZero) {
    var carryIn = this.HasFlag(Flags.Carry) ? 1 : 0;
    var carry = (value & 0x80) != 0;
    var result = (byte)((value << 1) | carryIn);
    this.SetFlags(affectZero && result == 0, false, false, carry);
    return result;
  }

  private byte Rr(byte value, bool affectZero) {
    var carryIn = this.HasFlag(Flags.Carry) ? 0x80 : 0;
    var carry = (value & 0x01) != 0;
    var result = (byte)((value >> 1) | carryIn);
    this.SetFlags(affectZero && result == 0, false, false, carry);
    return result;
  }

  private byte Sla(byte value) {
    var carry = (value & 0x80) != 0;
    var result = (byte)(value << 1);
    this.SetFlags(result == 0, false, false, carry);
    return result;
  }

  private byte Sra(byte value) {
    var carry = (value & 0x01) != 0;
    var result = (byte)((value >> 1) | (value & 0x80));
    this.SetFlags(result == 0, false, false, carry);
    return result;
  }

  private byte Srl(byte value) {
    var carry = (value & 0x01) != 0;
    var result = (byte)(value >> 1);
    this.SetFlags(result == 0, false, false, carry);
    return result;
  }

  private byte Swap(byte value) {
    var result = (byte)((value >> 4) | (value << 4));
    this.SetFlags(result == 0, false, false, false);
    return result;
  }

  private void BitTest(int bit, byte value) {
    this.SetFlag(Flags.Zero, (value & (1 << bit)) == 0);
    this.SetFlag(Flags.Subtract, false);
    this.SetFlag(Flags.HalfCarry, true);
  }

  // ── conditional helpers ─────────────────────────────────────────────────────────

  private long JrConditional(bool condition) {
    var offset = (sbyte)this.Fetch();
    if (!condition)
      return 8;
    this.PC = (ushort)(this.PC + offset);
    return 12;
  }

  private long JpConditional(bool condition) {
    var target = this.FetchWord();
    if (!condition)
      return 12;
    this.PC = target;
    return 16;
  }

  private long CallConditional(bool condition) {
    var target = this.FetchWord();
    if (!condition)
      return 12;
    this.Push(this.PC);
    this.PC = target;
    return 24;
  }

  private long RetConditional(bool condition) {
    if (!condition)
      return 8;
    this.PC = this.Pop();
    return 20;
  }

  private long Rst(ushort vector) {
    this.Push(this.PC);
    this.PC = vector;
    return 16;
  }

  /// <summary>
  /// Executes one instruction and returns the number of T-state clock cycles it consumed,
  /// including conditional-branch penalties.
  /// </summary>
  public long Step() {
    var opcode = this.Fetch();
    switch (opcode) {
      case 0x00: return 4;                                   // NOP
      case 0x10: this.Fetch(); return 4;                      // STOP (skips its 0x00 padding byte)
      case 0x76: return 4;                                   // HALT (NOP-with-cycles, see class doc)

      // ── 16-bit immediate loads ──
      case 0x01: this.BC = this.FetchWord(); return 12;
      case 0x11: this.DE = this.FetchWord(); return 12;
      case 0x21: this.HL = this.FetchWord(); return 12;
      case 0x31: this.SP = this.FetchWord(); return 12;

      // ── LD (a16),SP ──
      case 0x08: {
        var addr = this.FetchWord();
        this.Write(addr, (byte)this.SP);
        this.Write((ushort)(addr + 1), (byte)(this.SP >> 8));
        return 20;
      }

      // ── store/load A via register-pair pointers ──
      case 0x02: this.Write(this.BC, this.A); return 8;
      case 0x12: this.Write(this.DE, this.A); return 8;
      case 0x22: this.Write(this.HL, this.A); this.HL++; return 8;   // LD (HL+),A
      case 0x32: this.Write(this.HL, this.A); this.HL--; return 8;   // LD (HL-),A
      case 0x0A: this.A = this.Read(this.BC); return 8;
      case 0x1A: this.A = this.Read(this.DE); return 8;
      case 0x2A: this.A = this.Read(this.HL); this.HL++; return 8;   // LD A,(HL+)
      case 0x3A: this.A = this.Read(this.HL); this.HL--; return 8;   // LD A,(HL-)

      // ── 8-bit immediate loads ──
      case 0x06: this.B = this.Fetch(); return 8;
      case 0x0E: this.C = this.Fetch(); return 8;
      case 0x16: this.D = this.Fetch(); return 8;
      case 0x1E: this.E = this.Fetch(); return 8;
      case 0x26: this.H = this.Fetch(); return 8;
      case 0x2E: this.L = this.Fetch(); return 8;
      case 0x36: this.Write(this.HL, this.Fetch()); return 12;
      case 0x3E: this.A = this.Fetch(); return 8;

      // ── 16-bit INC/DEC ──
      case 0x03: this.BC++; return 8;
      case 0x13: this.DE++; return 8;
      case 0x23: this.HL++; return 8;
      case 0x33: this.SP++; return 8;
      case 0x0B: this.BC--; return 8;
      case 0x1B: this.DE--; return 8;
      case 0x2B: this.HL--; return 8;
      case 0x3B: this.SP--; return 8;

      // ── 8-bit INC ──
      case 0x04: this.B = this.Inc8(this.B); return 4;
      case 0x0C: this.C = this.Inc8(this.C); return 4;
      case 0x14: this.D = this.Inc8(this.D); return 4;
      case 0x1C: this.E = this.Inc8(this.E); return 4;
      case 0x24: this.H = this.Inc8(this.H); return 4;
      case 0x2C: this.L = this.Inc8(this.L); return 4;
      case 0x34: this.Write(this.HL, this.Inc8(this.Read(this.HL))); return 12;
      case 0x3C: this.A = this.Inc8(this.A); return 4;

      // ── 8-bit DEC ──
      case 0x05: this.B = this.Dec8(this.B); return 4;
      case 0x0D: this.C = this.Dec8(this.C); return 4;
      case 0x15: this.D = this.Dec8(this.D); return 4;
      case 0x1D: this.E = this.Dec8(this.E); return 4;
      case 0x25: this.H = this.Dec8(this.H); return 4;
      case 0x2D: this.L = this.Dec8(this.L); return 4;
      case 0x35: this.Write(this.HL, this.Dec8(this.Read(this.HL))); return 12;
      case 0x3D: this.A = this.Dec8(this.A); return 4;

      // ── ADD HL,rr ──
      case 0x09: this.AddHl(this.BC); return 8;
      case 0x19: this.AddHl(this.DE); return 8;
      case 0x29: this.AddHl(this.HL); return 8;
      case 0x39: this.AddHl(this.SP); return 8;

      // ── rotates on A ──
      case 0x07: this.A = this.Rlc(this.A, affectZero: false); return 4; // RLCA
      case 0x0F: this.A = this.Rrc(this.A, affectZero: false); return 4; // RRCA
      case 0x17: this.A = this.Rl(this.A, affectZero: false); return 4;  // RLA
      case 0x1F: this.A = this.Rr(this.A, affectZero: false); return 4;  // RRA

      // ── misc accumulator/flag ops ──
      case 0x27: this.Daa(); return 4;                                                   // DAA
      case 0x2F: this.A = (byte)~this.A; this.SetFlag(Flags.Subtract, true); this.SetFlag(Flags.HalfCarry, true); return 4; // CPL
      case 0x37: this.SetFlag(Flags.Subtract, false); this.SetFlag(Flags.HalfCarry, false); this.SetFlag(Flags.Carry, true); return 4; // SCF
      case 0x3F: this.SetFlag(Flags.Subtract, false); this.SetFlag(Flags.HalfCarry, false); this.SetFlag(Flags.Carry, !this.HasFlag(Flags.Carry)); return 4; // CCF

      // ── JR ──
      case 0x18: return this.JrConditional(true);
      case 0x20: return this.JrConditional(!this.HasFlag(Flags.Zero));
      case 0x28: return this.JrConditional(this.HasFlag(Flags.Zero));
      case 0x30: return this.JrConditional(!this.HasFlag(Flags.Carry));
      case 0x38: return this.JrConditional(this.HasFlag(Flags.Carry));

      // ── LD r,r' block ($40-$7F, excluding $76 HALT handled above) ──
      case >= 0x40 and <= 0x7F: return this.LoadRegToReg(opcode);

      // ── ALU block ($80-$BF) ──
      case >= 0x80 and <= 0xBF: return this.AluBlock(opcode);

      // ── RET conditional / unconditional / RETI ──
      case 0xC0: return this.RetConditional(!this.HasFlag(Flags.Zero));
      case 0xC8: return this.RetConditional(this.HasFlag(Flags.Zero));
      case 0xD0: return this.RetConditional(!this.HasFlag(Flags.Carry));
      case 0xD8: return this.RetConditional(this.HasFlag(Flags.Carry));
      case 0xC9: this.PC = this.Pop(); return 16;                       // RET
      case 0xD9: this.PC = this.Pop(); this.Ime = true; return 16;      // RETI

      // ── POP ──
      case 0xC1: this.BC = this.Pop(); return 12;
      case 0xD1: this.DE = this.Pop(); return 12;
      case 0xE1: this.HL = this.Pop(); return 12;
      case 0xF1: this.AF = this.Pop(); return 12;

      // ── PUSH ──
      case 0xC5: this.Push(this.BC); return 16;
      case 0xD5: this.Push(this.DE); return 16;
      case 0xE5: this.Push(this.HL); return 16;
      case 0xF5: this.Push(this.AF); return 16;

      // ── JP ──
      case 0xC2: return this.JpConditional(!this.HasFlag(Flags.Zero));
      case 0xCA: return this.JpConditional(this.HasFlag(Flags.Zero));
      case 0xD2: return this.JpConditional(!this.HasFlag(Flags.Carry));
      case 0xDA: return this.JpConditional(this.HasFlag(Flags.Carry));
      case 0xC3: this.PC = this.FetchWord(); return 16;                 // JP a16
      case 0xE9: this.PC = this.HL; return 4;                           // JP (HL)

      // ── CALL ──
      case 0xC4: return this.CallConditional(!this.HasFlag(Flags.Zero));
      case 0xCC: return this.CallConditional(this.HasFlag(Flags.Zero));
      case 0xD4: return this.CallConditional(!this.HasFlag(Flags.Carry));
      case 0xDC: return this.CallConditional(this.HasFlag(Flags.Carry));
      case 0xCD: { var t = this.FetchWord(); this.Push(this.PC); this.PC = t; return 24; } // CALL a16

      // ── RST ──
      case 0xC7: return this.Rst(0x00);
      case 0xCF: return this.Rst(0x08);
      case 0xD7: return this.Rst(0x10);
      case 0xDF: return this.Rst(0x18);
      case 0xE7: return this.Rst(0x20);
      case 0xEF: return this.Rst(0x28);
      case 0xF7: return this.Rst(0x30);
      case 0xFF: return this.Rst(0x38);

      // ── ALU with immediate ──
      case 0xC6: this.AddA(this.Fetch()); return 8;
      case 0xCE: this.AdcA(this.Fetch()); return 8;
      case 0xD6: this.SubA(this.Fetch()); return 8;
      case 0xDE: this.SbcA(this.Fetch()); return 8;
      case 0xE6: this.AndA(this.Fetch()); return 8;
      case 0xEE: this.XorA(this.Fetch()); return 8;
      case 0xF6: this.OrA(this.Fetch()); return 8;
      case 0xFE: this.CpA(this.Fetch()); return 8;

      // ── high-page I/O loads ──
      case 0xE0: this.Write((ushort)(0xFF00 + this.Fetch()), this.A); return 12;   // LDH (a8),A
      case 0xF0: this.A = this.Read((ushort)(0xFF00 + this.Fetch())); return 12;   // LDH A,(a8)
      case 0xE2: this.Write((ushort)(0xFF00 + this.C), this.A); return 8;          // LD (C),A
      case 0xF2: this.A = this.Read((ushort)(0xFF00 + this.C)); return 8;          // LD A,(C)
      case 0xEA: this.Write(this.FetchWord(), this.A); return 16;                  // LD (a16),A
      case 0xFA: this.A = this.Read(this.FetchWord()); return 16;                  // LD A,(a16)

      // ── SP arithmetic / loads ──
      case 0xE8: this.SP = this.AddSpSigned((sbyte)this.Fetch()); return 16;       // ADD SP,r8
      case 0xF8: this.HL = this.AddSpSigned((sbyte)this.Fetch()); return 12;       // LD HL,SP+r8
      case 0xF9: this.SP = this.HL; return 8;                                      // LD SP,HL

      // ── interrupt master flag ──
      case 0xF3: this.Ime = false; return 4;   // DI
      case 0xFB: this.Ime = true; return 4;    // EI

      // ── CB prefix ──
      case 0xCB: return this.StepCb();

      // The eight illegal opcodes ($D3 $DB $DD $E3 $E4 $EB $EC $ED $F4 $FC $FD) fall through
      // here and are decoded as 4-cycle no-ops (see the class summary).
      default:
        return 4;
    }
  }

  // ── register selector helpers (3-bit fields in $40-$BF and CB opcodes) ──────────

  private byte GetReg(int sel) => sel switch {
    0 => this.B,
    1 => this.C,
    2 => this.D,
    3 => this.E,
    4 => this.H,
    5 => this.L,
    6 => this.Read(this.HL),
    _ => this.A,
  };

  private void SetReg(int sel, byte value) {
    switch (sel) {
      case 0: this.B = value; break;
      case 1: this.C = value; break;
      case 2: this.D = value; break;
      case 3: this.E = value; break;
      case 4: this.H = value; break;
      case 5: this.L = value; break;
      case 6: this.Write(this.HL, value); break;
      default: this.A = value; break;
    }
  }

  private long LoadRegToReg(byte opcode) {
    var dst = (opcode >> 3) & 0x07;
    var src = opcode & 0x07;
    this.SetReg(dst, this.GetReg(src));
    // (HL) source or destination costs an extra memory cycle.
    return src == 6 || dst == 6 ? 8 : 4;
  }

  private long AluBlock(byte opcode) {
    var src = opcode & 0x07;
    var value = this.GetReg(src);
    switch ((opcode >> 3) & 0x07) {
      case 0: this.AddA(value); break;
      case 1: this.AdcA(value); break;
      case 2: this.SubA(value); break;
      case 3: this.SbcA(value); break;
      case 4: this.AndA(value); break;
      case 5: this.XorA(value); break;
      case 6: this.OrA(value); break;
      default: this.CpA(value); break;
    }
    return src == 6 ? 8 : 4;
  }

  private long StepCb() {
    var opcode = this.Fetch();
    var sel = opcode & 0x07;
    var op = (opcode >> 3) & 0x07;
    var group = (opcode >> 6) & 0x03;
    var isHl = sel == 6;

    if (group == 1) {
      // BIT b,r — no write-back; (HL) form is 12 cycles, register form 8.
      this.BitTest(op, this.GetReg(sel));
      return isHl ? 12 : 8;
    }

    var value = this.GetReg(sel);
    byte result;
    if (group == 0) {
      result = op switch {
        0 => this.Rlc(value, affectZero: true),
        1 => this.Rrc(value, affectZero: true),
        2 => this.Rl(value, affectZero: true),
        3 => this.Rr(value, affectZero: true),
        4 => this.Sla(value),
        5 => this.Sra(value),
        6 => this.Swap(value),
        _ => this.Srl(value),
      };
    } else if (group == 2) {
      result = (byte)(value & ~(1 << op)); // RES b,r
    } else {
      result = (byte)(value | (1 << op));  // SET b,r
    }

    this.SetReg(sel, result);
    return isHl ? 16 : 8;
  }
}
