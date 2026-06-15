#pragma warning disable CS1591
namespace Codec.Mos6502;

/// <summary>
/// A cycle-counting NMOS 6502 / 6510 CPU core. The instruction decode covers every
/// official opcode plus the stable illegal opcodes that SID/NSF players are known to
/// rely on: <c>LAX, SAX, DCP, ISC, SLO, RLA, SRE, RRA, ANC, ALR, ARR, AXS</c> and the
/// undocumented <c>NOP</c>/<c>SKB</c>/<c>SKW</c> family. Decimal mode (BCD) is implemented
/// for <c>ADC</c>/<c>SBC</c> because C64 tunes use it.
/// <para>Behaviours intentionally omitted (they are unstable on real silicon and not used
/// by music players): the highly analog/unstable illegals <c>ANE/XAA ($8B)</c>,
/// <c>LXA/LAX#imm ($AB)</c>, <c>TAS/SHS ($9B)</c>, <c>SHA/AHX ($9F/$93)</c>,
/// <c>SHX ($9E)</c>, <c>SHY ($9C)</c> and the <c>KIL/JAM</c> halts are decoded as
/// no-ops/best-effort and flagged. The undocumented decimal flags of ADC/SBC (N,V,Z in
/// BCD mode) follow the NMOS results.</para>
/// Memory is accessed exclusively through <see cref="IBus6502"/>.
/// </summary>
public sealed class Cpu6502 {

  /// <summary>Processor status flag bits.</summary>
  [Flags]
  public enum Status : byte {
    Carry = 0x01,
    Zero = 0x02,
    Interrupt = 0x04,
    Decimal = 0x08,
    Break = 0x10,
    Unused = 0x20,
    Overflow = 0x40,
    Negative = 0x80,
  }

  private readonly IBus6502 _bus;

  public byte A;
  public byte X;
  public byte Y;
  public byte SP;
  public ushort PC;
  public Status P;

  public Cpu6502(IBus6502 bus) {
    this._bus = bus;
    this.Reset();
  }

  /// <summary>
  /// Power-on/reset: stack pointer to $FD, interrupt-disable set, PC loaded from the
  /// reset vector at $FFFC/$FFFD.
  /// </summary>
  public void Reset() {
    this.A = this.X = this.Y = 0;
    this.SP = 0xFD;
    this.P = Status.Interrupt | Status.Unused;
    this.PC = (ushort)(this._bus.Read(0xFFFC) | (this._bus.Read(0xFFFD) << 8));
  }

  // ── bus helpers ─────────────────────────────────────────────────────────────

  private byte Read(ushort addr) => this._bus.Read(addr);
  private void Write(ushort addr, byte value) => this._bus.Write(addr, value);

  private byte Fetch() => this.Read(this.PC++);

  private ushort FetchWord() {
    var lo = this.Fetch();
    var hi = this.Fetch();
    return (ushort)(lo | (hi << 8));
  }

  private void Push(byte value) => this.Write((ushort)(0x0100 | this.SP--), value);
  private byte Pop() => this.Read((ushort)(0x0100 | ++this.SP));

  private void PushWord(ushort value) {
    this.Push((byte)(value >> 8));
    this.Push((byte)value);
  }

  private ushort PopWord() {
    var lo = this.Pop();
    var hi = this.Pop();
    return (ushort)(lo | (hi << 8));
  }

  // ── flag helpers ────────────────────────────────────────────────────────────

  private void SetFlag(Status flag, bool on) {
    if (on) this.P |= flag; else this.P &= ~flag;
  }

  private bool HasFlag(Status flag) => (this.P & flag) != 0;

  private void SetZN(byte value) {
    this.SetFlag(Status.Zero, value == 0);
    this.SetFlag(Status.Negative, (value & 0x80) != 0);
  }

  /// <summary>
  /// Calls into a subroutine at <paramref name="address"/> using the player convention:
  /// a sentinel return address is pushed so the matching <c>RTS</c> lands on a known PC,
  /// at which point execution stops. Returns the cycles consumed (capped). Used by SID/NSF
  /// players to invoke the tune's init and play routines.
  /// </summary>
  public long RunUntilRts(ushort address, long maxCycles) {
    // Push a sentinel return address; the routine's RTS will pull (sentinel) and land on
    // (sentinel + 1). We detect that PC and stop. 0x0000 is chosen as the sentinel so the
    // resulting PC is 0x0001.
    const ushort sentinel = 0x0000;
    var targetStack = this.SP;
    this.PushWord(sentinel);
    this.PC = address;

    long cycles = 0;
    while (cycles < maxCycles) {
      // Stop once the matching RTS has popped the sentinel (stack back above target and
      // PC at sentinel+1).
      if (this.PC == sentinel + 1 && this.SP == targetStack)
        break;
      cycles += this.Step();
    }
    return cycles;
  }

  // ── addressing-mode operand resolution ──────────────────────────────────────
  //
  // Each helper returns the effective address and reports whether a page boundary was
  // crossed (the extra-cycle penalty on indexed reads). Write/RMW instructions always pay
  // the indexing cycle regardless of crossing; that is handled per-opcode.

  private ushort AddrZeroPage() => this.Fetch();
  private ushort AddrZeroPageX() => (ushort)((this.Fetch() + this.X) & 0xFF);
  private ushort AddrZeroPageY() => (ushort)((this.Fetch() + this.Y) & 0xFF);
  private ushort AddrAbsolute() => this.FetchWord();

  private ushort AddrAbsoluteX(out bool crossed) {
    var baseAddr = this.FetchWord();
    var addr = (ushort)(baseAddr + this.X);
    crossed = (baseAddr & 0xFF00) != (addr & 0xFF00);
    return addr;
  }

  private ushort AddrAbsoluteY(out bool crossed) {
    var baseAddr = this.FetchWord();
    var addr = (ushort)(baseAddr + this.Y);
    crossed = (baseAddr & 0xFF00) != (addr & 0xFF00);
    return addr;
  }

  private ushort AddrIndexedIndirect() {
    // (zp,X): zero-page pointer wraps within the zero page.
    var ptr = (byte)(this.Fetch() + this.X);
    var lo = this.Read(ptr);
    var hi = this.Read((byte)(ptr + 1));
    return (ushort)(lo | (hi << 8));
  }

  private ushort AddrIndirectIndexed(out bool crossed) {
    // (zp),Y: read 16-bit pointer from zero page (wrapping), then add Y.
    var ptr = this.Fetch();
    var lo = this.Read(ptr);
    var hi = this.Read((byte)(ptr + 1));
    var baseAddr = (ushort)(lo | (hi << 8));
    var addr = (ushort)(baseAddr + this.Y);
    crossed = (baseAddr & 0xFF00) != (addr & 0xFF00);
    return addr;
  }

  // ── ALU primitives ──────────────────────────────────────────────────────────

  private void Adc(byte value) {
    if (this.HasFlag(Status.Decimal)) {
      this.AdcDecimal(value);
      return;
    }
    var carry = this.HasFlag(Status.Carry) ? 1 : 0;
    var sum = this.A + value + carry;
    var result = (byte)sum;
    this.SetFlag(Status.Carry, sum > 0xFF);
    this.SetFlag(Status.Overflow, ((this.A ^ result) & (value ^ result) & 0x80) != 0);
    this.A = result;
    this.SetZN(result);
  }

  private void AdcDecimal(byte value) {
    // NMOS BCD ADC. Z is computed from the binary sum; N and V from the high-nibble
    // adjustment (the documented NMOS quirk).
    var carry = this.HasFlag(Status.Carry) ? 1 : 0;
    var binSum = this.A + value + carry;
    this.SetFlag(Status.Zero, (binSum & 0xFF) == 0);

    var lo = (this.A & 0x0F) + (value & 0x0F) + carry;
    var hi = (this.A & 0xF0) + (value & 0xF0);
    if (lo > 0x09) {
      hi += 0x10;
      lo += 0x06;
    }
    this.SetFlag(Status.Negative, (hi & 0x80) != 0);
    this.SetFlag(Status.Overflow, ((this.A ^ hi) & (value ^ hi) & 0x80) != 0);
    if (hi > 0x90)
      hi += 0x60;
    this.SetFlag(Status.Carry, hi > 0xFF);
    this.A = (byte)((lo & 0x0F) | (hi & 0xF0));
  }

  private void Sbc(byte value) {
    if (this.HasFlag(Status.Decimal)) {
      this.SbcDecimal(value);
      return;
    }
    // Binary SBC is ADC of the one's complement.
    var carry = this.HasFlag(Status.Carry) ? 1 : 0;
    var diff = this.A - value - (1 - carry);
    var result = (byte)diff;
    this.SetFlag(Status.Carry, diff >= 0);
    this.SetFlag(Status.Overflow, ((this.A ^ value) & (this.A ^ result) & 0x80) != 0);
    this.A = result;
    this.SetZN(result);
  }

  private void SbcDecimal(byte value) {
    var carry = this.HasFlag(Status.Carry) ? 1 : 0;
    var binDiff = this.A - value - (1 - carry);

    var lo = (this.A & 0x0F) - (value & 0x0F) - (1 - carry);
    var hi = (this.A & 0xF0) - (value & 0xF0);
    if ((lo & 0x10) != 0) {
      lo -= 0x06;
      hi -= 0x10;
    }
    if ((hi & 0x0100) != 0)
      hi -= 0x60;

    // Flags follow the binary subtraction (NMOS).
    var result = (byte)binDiff;
    this.SetFlag(Status.Carry, binDiff >= 0);
    this.SetFlag(Status.Overflow, ((this.A ^ value) & (this.A ^ result) & 0x80) != 0);
    this.SetZN(result);
    this.A = (byte)((lo & 0x0F) | (hi & 0xF0));
  }

  private void Compare(byte register, byte value) {
    var diff = register - value;
    this.SetFlag(Status.Carry, register >= value);
    this.SetZN((byte)diff);
  }

  private void And(byte value) { this.A &= value; this.SetZN(this.A); }
  private void Ora(byte value) { this.A |= value; this.SetZN(this.A); }
  private void Eor(byte value) { this.A ^= value; this.SetZN(this.A); }

  private void Bit(byte value) {
    this.SetFlag(Status.Zero, (this.A & value) == 0);
    this.SetFlag(Status.Negative, (value & 0x80) != 0);
    this.SetFlag(Status.Overflow, (value & 0x40) != 0);
  }

  private byte Asl(byte value) {
    this.SetFlag(Status.Carry, (value & 0x80) != 0);
    var result = (byte)(value << 1);
    this.SetZN(result);
    return result;
  }

  private byte Lsr(byte value) {
    this.SetFlag(Status.Carry, (value & 0x01) != 0);
    var result = (byte)(value >> 1);
    this.SetZN(result);
    return result;
  }

  private byte Rol(byte value) {
    var carryIn = this.HasFlag(Status.Carry) ? 1 : 0;
    this.SetFlag(Status.Carry, (value & 0x80) != 0);
    var result = (byte)((value << 1) | carryIn);
    this.SetZN(result);
    return result;
  }

  private byte Ror(byte value) {
    var carryIn = this.HasFlag(Status.Carry) ? 0x80 : 0;
    this.SetFlag(Status.Carry, (value & 0x01) != 0);
    var result = (byte)((value >> 1) | carryIn);
    this.SetZN(result);
    return result;
  }

  private long Branch(bool condition) {
    var offset = (sbyte)this.Fetch();
    if (!condition)
      return 2;
    var target = (ushort)(this.PC + offset);
    var extra = (this.PC & 0xFF00) != (target & 0xFF00) ? 2 : 1;
    this.PC = target;
    return 2 + extra;
  }

  // ── instruction dispatch ─────────────────────────────────────────────────────

  /// <summary>
  /// Executes one instruction and returns the number of clock cycles it consumed,
  /// including page-cross and branch-taken penalties.
  /// </summary>
  public long Step() {
    var opcode = this.Fetch();
    switch (opcode) {
      // ── LDA ──
      case 0xA9: this.A = this.Fetch(); this.SetZN(this.A); return 2;
      case 0xA5: this.A = this.Read(this.AddrZeroPage()); this.SetZN(this.A); return 3;
      case 0xB5: this.A = this.Read(this.AddrZeroPageX()); this.SetZN(this.A); return 4;
      case 0xAD: this.A = this.Read(this.AddrAbsolute()); this.SetZN(this.A); return 4;
      case 0xBD: { this.A = this.Read(this.AddrAbsoluteX(out var c)); this.SetZN(this.A); return c ? 5 : 4; }
      case 0xB9: { this.A = this.Read(this.AddrAbsoluteY(out var c)); this.SetZN(this.A); return c ? 5 : 4; }
      case 0xA1: this.A = this.Read(this.AddrIndexedIndirect()); this.SetZN(this.A); return 6;
      case 0xB1: { this.A = this.Read(this.AddrIndirectIndexed(out var c)); this.SetZN(this.A); return c ? 6 : 5; }

      // ── LDX ──
      case 0xA2: this.X = this.Fetch(); this.SetZN(this.X); return 2;
      case 0xA6: this.X = this.Read(this.AddrZeroPage()); this.SetZN(this.X); return 3;
      case 0xB6: this.X = this.Read(this.AddrZeroPageY()); this.SetZN(this.X); return 4;
      case 0xAE: this.X = this.Read(this.AddrAbsolute()); this.SetZN(this.X); return 4;
      case 0xBE: { this.X = this.Read(this.AddrAbsoluteY(out var c)); this.SetZN(this.X); return c ? 5 : 4; }

      // ── LDY ──
      case 0xA0: this.Y = this.Fetch(); this.SetZN(this.Y); return 2;
      case 0xA4: this.Y = this.Read(this.AddrZeroPage()); this.SetZN(this.Y); return 3;
      case 0xB4: this.Y = this.Read(this.AddrZeroPageX()); this.SetZN(this.Y); return 4;
      case 0xAC: this.Y = this.Read(this.AddrAbsolute()); this.SetZN(this.Y); return 4;
      case 0xBC: { this.Y = this.Read(this.AddrAbsoluteX(out var c)); this.SetZN(this.Y); return c ? 5 : 4; }

      // ── STA ──
      case 0x85: this.Write(this.AddrZeroPage(), this.A); return 3;
      case 0x95: this.Write(this.AddrZeroPageX(), this.A); return 4;
      case 0x8D: this.Write(this.AddrAbsolute(), this.A); return 4;
      case 0x9D: { var a = this.AddrAbsoluteX(out _); this.Write(a, this.A); return 5; }
      case 0x99: { var a = this.AddrAbsoluteY(out _); this.Write(a, this.A); return 5; }
      case 0x81: this.Write(this.AddrIndexedIndirect(), this.A); return 6;
      case 0x91: { var a = this.AddrIndirectIndexed(out _); this.Write(a, this.A); return 6; }

      // ── STX / STY ──
      case 0x86: this.Write(this.AddrZeroPage(), this.X); return 3;
      case 0x96: this.Write(this.AddrZeroPageY(), this.X); return 4;
      case 0x8E: this.Write(this.AddrAbsolute(), this.X); return 4;
      case 0x84: this.Write(this.AddrZeroPage(), this.Y); return 3;
      case 0x94: this.Write(this.AddrZeroPageX(), this.Y); return 4;
      case 0x8C: this.Write(this.AddrAbsolute(), this.Y); return 4;

      // ── transfers ──
      case 0xAA: this.X = this.A; this.SetZN(this.X); return 2; // TAX
      case 0xA8: this.Y = this.A; this.SetZN(this.Y); return 2; // TAY
      case 0x8A: this.A = this.X; this.SetZN(this.A); return 2; // TXA
      case 0x98: this.A = this.Y; this.SetZN(this.A); return 2; // TYA
      case 0xBA: this.X = this.SP; this.SetZN(this.X); return 2; // TSX
      case 0x9A: this.SP = this.X; return 2;                     // TXS

      // ── stack ──
      case 0x48: this.Push(this.A); return 3;                                      // PHA
      case 0x68: this.A = this.Pop(); this.SetZN(this.A); return 4;                 // PLA
      case 0x08: this.Push((byte)(this.P | Status.Break | Status.Unused)); return 3; // PHP
      case 0x28: this.P = (Status)(this.Pop() & ~(byte)Status.Break) | Status.Unused; return 4; // PLP

      // ── logic (immediate / memory) ──
      case 0x29: this.And(this.Fetch()); return 2;
      case 0x25: this.And(this.Read(this.AddrZeroPage())); return 3;
      case 0x35: this.And(this.Read(this.AddrZeroPageX())); return 4;
      case 0x2D: this.And(this.Read(this.AddrAbsolute())); return 4;
      case 0x3D: { var v = this.Read(this.AddrAbsoluteX(out var c)); this.And(v); return c ? 5 : 4; }
      case 0x39: { var v = this.Read(this.AddrAbsoluteY(out var c)); this.And(v); return c ? 5 : 4; }
      case 0x21: this.And(this.Read(this.AddrIndexedIndirect())); return 6;
      case 0x31: { var v = this.Read(this.AddrIndirectIndexed(out var c)); this.And(v); return c ? 6 : 5; }

      case 0x09: this.Ora(this.Fetch()); return 2;
      case 0x05: this.Ora(this.Read(this.AddrZeroPage())); return 3;
      case 0x15: this.Ora(this.Read(this.AddrZeroPageX())); return 4;
      case 0x0D: this.Ora(this.Read(this.AddrAbsolute())); return 4;
      case 0x1D: { var v = this.Read(this.AddrAbsoluteX(out var c)); this.Ora(v); return c ? 5 : 4; }
      case 0x19: { var v = this.Read(this.AddrAbsoluteY(out var c)); this.Ora(v); return c ? 5 : 4; }
      case 0x01: this.Ora(this.Read(this.AddrIndexedIndirect())); return 6;
      case 0x11: { var v = this.Read(this.AddrIndirectIndexed(out var c)); this.Ora(v); return c ? 6 : 5; }

      case 0x49: this.Eor(this.Fetch()); return 2;
      case 0x45: this.Eor(this.Read(this.AddrZeroPage())); return 3;
      case 0x55: this.Eor(this.Read(this.AddrZeroPageX())); return 4;
      case 0x4D: this.Eor(this.Read(this.AddrAbsolute())); return 4;
      case 0x5D: { var v = this.Read(this.AddrAbsoluteX(out var c)); this.Eor(v); return c ? 5 : 4; }
      case 0x59: { var v = this.Read(this.AddrAbsoluteY(out var c)); this.Eor(v); return c ? 5 : 4; }
      case 0x41: this.Eor(this.Read(this.AddrIndexedIndirect())); return 6;
      case 0x51: { var v = this.Read(this.AddrIndirectIndexed(out var c)); this.Eor(v); return c ? 6 : 5; }

      // ── BIT ──
      case 0x24: this.Bit(this.Read(this.AddrZeroPage())); return 3;
      case 0x2C: this.Bit(this.Read(this.AddrAbsolute())); return 4;

      // ── ADC ──
      case 0x69: this.Adc(this.Fetch()); return 2;
      case 0x65: this.Adc(this.Read(this.AddrZeroPage())); return 3;
      case 0x75: this.Adc(this.Read(this.AddrZeroPageX())); return 4;
      case 0x6D: this.Adc(this.Read(this.AddrAbsolute())); return 4;
      case 0x7D: { var v = this.Read(this.AddrAbsoluteX(out var c)); this.Adc(v); return c ? 5 : 4; }
      case 0x79: { var v = this.Read(this.AddrAbsoluteY(out var c)); this.Adc(v); return c ? 5 : 4; }
      case 0x61: this.Adc(this.Read(this.AddrIndexedIndirect())); return 6;
      case 0x71: { var v = this.Read(this.AddrIndirectIndexed(out var c)); this.Adc(v); return c ? 6 : 5; }

      // ── SBC ──
      case 0xE9: case 0xEB: this.Sbc(this.Fetch()); return 2; // $EB = undocumented alias of SBC#
      case 0xE5: this.Sbc(this.Read(this.AddrZeroPage())); return 3;
      case 0xF5: this.Sbc(this.Read(this.AddrZeroPageX())); return 4;
      case 0xED: this.Sbc(this.Read(this.AddrAbsolute())); return 4;
      case 0xFD: { var v = this.Read(this.AddrAbsoluteX(out var c)); this.Sbc(v); return c ? 5 : 4; }
      case 0xF9: { var v = this.Read(this.AddrAbsoluteY(out var c)); this.Sbc(v); return c ? 5 : 4; }
      case 0xE1: this.Sbc(this.Read(this.AddrIndexedIndirect())); return 6;
      case 0xF1: { var v = this.Read(this.AddrIndirectIndexed(out var c)); this.Sbc(v); return c ? 6 : 5; }

      // ── CMP ──
      case 0xC9: this.Compare(this.A, this.Fetch()); return 2;
      case 0xC5: this.Compare(this.A, this.Read(this.AddrZeroPage())); return 3;
      case 0xD5: this.Compare(this.A, this.Read(this.AddrZeroPageX())); return 4;
      case 0xCD: this.Compare(this.A, this.Read(this.AddrAbsolute())); return 4;
      case 0xDD: { var v = this.Read(this.AddrAbsoluteX(out var c)); this.Compare(this.A, v); return c ? 5 : 4; }
      case 0xD9: { var v = this.Read(this.AddrAbsoluteY(out var c)); this.Compare(this.A, v); return c ? 5 : 4; }
      case 0xC1: this.Compare(this.A, this.Read(this.AddrIndexedIndirect())); return 6;
      case 0xD1: { var v = this.Read(this.AddrIndirectIndexed(out var c)); this.Compare(this.A, v); return c ? 6 : 5; }

      // ── CPX / CPY ──
      case 0xE0: this.Compare(this.X, this.Fetch()); return 2;
      case 0xE4: this.Compare(this.X, this.Read(this.AddrZeroPage())); return 3;
      case 0xEC: this.Compare(this.X, this.Read(this.AddrAbsolute())); return 4;
      case 0xC0: this.Compare(this.Y, this.Fetch()); return 2;
      case 0xC4: this.Compare(this.Y, this.Read(this.AddrZeroPage())); return 3;
      case 0xCC: this.Compare(this.Y, this.Read(this.AddrAbsolute())); return 4;

      // ── INC / DEC (memory) ──
      case 0xE6: return this.Rmw(this.AddrZeroPage(), this.IncFlagged, 5);
      case 0xF6: return this.Rmw(this.AddrZeroPageX(), this.IncFlagged, 6);
      case 0xEE: return this.Rmw(this.AddrAbsolute(), this.IncFlagged, 6);
      case 0xFE: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.IncFlagged, 7); }
      case 0xC6: return this.Rmw(this.AddrZeroPage(), this.DecFlagged, 5);
      case 0xD6: return this.Rmw(this.AddrZeroPageX(), this.DecFlagged, 6);
      case 0xCE: return this.Rmw(this.AddrAbsolute(), this.DecFlagged, 6);
      case 0xDE: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.DecFlagged, 7); }

      // ── INX/DEX/INY/DEY ──
      case 0xE8: this.X++; this.SetZN(this.X); return 2;
      case 0xCA: this.X--; this.SetZN(this.X); return 2;
      case 0xC8: this.Y++; this.SetZN(this.Y); return 2;
      case 0x88: this.Y--; this.SetZN(this.Y); return 2;

      // ── shifts/rotates (accumulator) ──
      case 0x0A: this.A = this.Asl(this.A); return 2;
      case 0x4A: this.A = this.Lsr(this.A); return 2;
      case 0x2A: this.A = this.Rol(this.A); return 2;
      case 0x6A: this.A = this.Ror(this.A); return 2;

      // ── ASL (memory) ──
      case 0x06: return this.Rmw(this.AddrZeroPage(), this.Asl, 5);
      case 0x16: return this.Rmw(this.AddrZeroPageX(), this.Asl, 6);
      case 0x0E: return this.Rmw(this.AddrAbsolute(), this.Asl, 6);
      case 0x1E: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.Asl, 7); }

      // ── LSR (memory) ──
      case 0x46: return this.Rmw(this.AddrZeroPage(), this.Lsr, 5);
      case 0x56: return this.Rmw(this.AddrZeroPageX(), this.Lsr, 6);
      case 0x4E: return this.Rmw(this.AddrAbsolute(), this.Lsr, 6);
      case 0x5E: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.Lsr, 7); }

      // ── ROL (memory) ──
      case 0x26: return this.Rmw(this.AddrZeroPage(), this.Rol, 5);
      case 0x36: return this.Rmw(this.AddrZeroPageX(), this.Rol, 6);
      case 0x2E: return this.Rmw(this.AddrAbsolute(), this.Rol, 6);
      case 0x3E: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.Rol, 7); }

      // ── ROR (memory) ──
      case 0x66: return this.Rmw(this.AddrZeroPage(), this.Ror, 5);
      case 0x76: return this.Rmw(this.AddrZeroPageX(), this.Ror, 6);
      case 0x6E: return this.Rmw(this.AddrAbsolute(), this.Ror, 6);
      case 0x7E: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.Ror, 7); }

      // ── jumps / subroutines ──
      case 0x4C: this.PC = this.AddrAbsolute(); return 3;                 // JMP abs
      case 0x6C: return this.JmpIndirect();                              // JMP (ind)
      case 0x20: return this.Jsr();                                       // JSR
      case 0x60: this.PC = (ushort)(this.PopWord() + 1); return 6;        // RTS
      case 0x40: return this.Rti();                                       // RTI
      case 0x00: return this.Brk();                                       // BRK

      // ── branches ──
      case 0x10: return this.Branch(!this.HasFlag(Status.Negative)); // BPL
      case 0x30: return this.Branch(this.HasFlag(Status.Negative));  // BMI
      case 0x50: return this.Branch(!this.HasFlag(Status.Overflow)); // BVC
      case 0x70: return this.Branch(this.HasFlag(Status.Overflow));  // BVS
      case 0x90: return this.Branch(!this.HasFlag(Status.Carry));    // BCC
      case 0xB0: return this.Branch(this.HasFlag(Status.Carry));     // BCS
      case 0xD0: return this.Branch(!this.HasFlag(Status.Zero));     // BNE
      case 0xF0: return this.Branch(this.HasFlag(Status.Zero));      // BEQ

      // ── flag set/clear ──
      case 0x18: this.SetFlag(Status.Carry, false); return 2;     // CLC
      case 0x38: this.SetFlag(Status.Carry, true); return 2;      // SEC
      case 0x58: this.SetFlag(Status.Interrupt, false); return 2; // CLI
      case 0x78: this.SetFlag(Status.Interrupt, true); return 2;  // SEI
      case 0xB8: this.SetFlag(Status.Overflow, false); return 2;  // CLV
      case 0xD8: this.SetFlag(Status.Decimal, false); return 2;   // CLD
      case 0xF8: this.SetFlag(Status.Decimal, true); return 2;    // SED

      // ── NOP (official) ──
      case 0xEA: return 2;

      default:
        return this.StepIllegal(opcode);
    }
  }

  // ── read-modify-write helper ────────────────────────────────────────────────

  private long Rmw(ushort addr, Func<byte, byte> op, long cycles) {
    var value = op(this.Read(addr));
    this.Write(addr, value);
    return cycles;
  }

  private byte IncFlagged(byte value) { var r = (byte)(value + 1); this.SetZN(r); return r; }
  private byte DecFlagged(byte value) { var r = (byte)(value - 1); this.SetZN(r); return r; }

  private long JmpIndirect() {
    var ptr = this.FetchWord();
    // NMOS page-boundary bug: the high byte is fetched from the same page.
    var lo = this.Read(ptr);
    var hiAddr = (ushort)((ptr & 0xFF00) | ((ptr + 1) & 0x00FF));
    var hi = this.Read(hiAddr);
    this.PC = (ushort)(lo | (hi << 8));
    return 5;
  }

  private long Jsr() {
    var target = this.FetchWord();
    // The pushed address is PC-1 (the last byte of the JSR operand).
    this.PushWord((ushort)(this.PC - 1));
    this.PC = target;
    return 6;
  }

  private long Rti() {
    this.P = (Status)(this.Pop() & ~(byte)Status.Break) | Status.Unused;
    this.PC = this.PopWord();
    return 6;
  }

  private long Brk() {
    this.PC++; // BRK has a padding byte.
    this.PushWord(this.PC);
    this.Push((byte)(this.P | Status.Break | Status.Unused));
    this.SetFlag(Status.Interrupt, true);
    this.PC = (ushort)(this.Read(0xFFFE) | (this.Read(0xFFFF) << 8));
    return 7;
  }

  // ── illegal/undocumented opcodes ────────────────────────────────────────────

  private long StepIllegal(byte opcode) {
    switch (opcode) {
      // ── undocumented NOPs ──
      // implied 2-cycle NOPs
      case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA:
        return 2;
      // immediate (SKB) NOPs — skip one byte
      case 0x80: case 0x82: case 0x89: case 0xC2: case 0xE2:
        this.Fetch(); return 2;
      // zero-page NOPs
      case 0x04: case 0x44: case 0x64:
        this.AddrZeroPage(); return 3;
      // zero-page,X NOPs
      case 0x14: case 0x34: case 0x54: case 0x74: case 0xD4: case 0xF4:
        this.AddrZeroPageX(); return 4;
      // absolute NOP (SKW)
      case 0x0C:
        this.AddrAbsolute(); return 4;
      // absolute,X NOPs (page-cross penalty)
      case 0x1C: case 0x3C: case 0x5C: case 0x7C: case 0xDC: case 0xFC: {
        this.AddrAbsoluteX(out var c); return c ? 5 : 4;
      }

      // ── LAX (load A and X) ──
      case 0xA7: { var v = this.Read(this.AddrZeroPage()); this.A = this.X = v; this.SetZN(v); return 3; }
      case 0xB7: { var v = this.Read(this.AddrZeroPageY()); this.A = this.X = v; this.SetZN(v); return 4; }
      case 0xAF: { var v = this.Read(this.AddrAbsolute()); this.A = this.X = v; this.SetZN(v); return 4; }
      case 0xBF: { var v = this.Read(this.AddrAbsoluteY(out var c)); this.A = this.X = v; this.SetZN(v); return c ? 5 : 4; }
      case 0xA3: { var v = this.Read(this.AddrIndexedIndirect()); this.A = this.X = v; this.SetZN(v); return 6; }
      case 0xB3: { var v = this.Read(this.AddrIndirectIndexed(out var c)); this.A = this.X = v; this.SetZN(v); return c ? 6 : 5; }

      // ── SAX (store A AND X) ──
      case 0x87: this.Write(this.AddrZeroPage(), (byte)(this.A & this.X)); return 3;
      case 0x97: this.Write(this.AddrZeroPageY(), (byte)(this.A & this.X)); return 4;
      case 0x8F: this.Write(this.AddrAbsolute(), (byte)(this.A & this.X)); return 4;
      case 0x83: this.Write(this.AddrIndexedIndirect(), (byte)(this.A & this.X)); return 6;

      // ── DCP (DEC then CMP) ──
      case 0xC7: return this.RmwIllegal(this.AddrZeroPage(), this.Dcp, 5);
      case 0xD7: return this.RmwIllegal(this.AddrZeroPageX(), this.Dcp, 6);
      case 0xCF: return this.RmwIllegal(this.AddrAbsolute(), this.Dcp, 6);
      case 0xDF: { var a = this.AddrAbsoluteX(out _); return this.RmwIllegal(a, this.Dcp, 7); }
      case 0xDB: { var a = this.AddrAbsoluteY(out _); return this.RmwIllegal(a, this.Dcp, 7); }
      case 0xC3: return this.RmwIllegal(this.AddrIndexedIndirect(), this.Dcp, 8);
      case 0xD3: { var a = this.AddrIndirectIndexed(out _); return this.RmwIllegal(a, this.Dcp, 8); }

      // ── ISC / ISB (INC then SBC) ──
      case 0xE7: return this.RmwIllegal(this.AddrZeroPage(), this.Isc, 5);
      case 0xF7: return this.RmwIllegal(this.AddrZeroPageX(), this.Isc, 6);
      case 0xEF: return this.RmwIllegal(this.AddrAbsolute(), this.Isc, 6);
      case 0xFF: { var a = this.AddrAbsoluteX(out _); return this.RmwIllegal(a, this.Isc, 7); }
      case 0xFB: { var a = this.AddrAbsoluteY(out _); return this.RmwIllegal(a, this.Isc, 7); }
      case 0xE3: return this.RmwIllegal(this.AddrIndexedIndirect(), this.Isc, 8);
      case 0xF3: { var a = this.AddrIndirectIndexed(out _); return this.RmwIllegal(a, this.Isc, 8); }

      // ── SLO (ASL then ORA) ──
      case 0x07: return this.RmwIllegal(this.AddrZeroPage(), this.Slo, 5);
      case 0x17: return this.RmwIllegal(this.AddrZeroPageX(), this.Slo, 6);
      case 0x0F: return this.RmwIllegal(this.AddrAbsolute(), this.Slo, 6);
      case 0x1F: { var a = this.AddrAbsoluteX(out _); return this.RmwIllegal(a, this.Slo, 7); }
      case 0x1B: { var a = this.AddrAbsoluteY(out _); return this.RmwIllegal(a, this.Slo, 7); }
      case 0x03: return this.RmwIllegal(this.AddrIndexedIndirect(), this.Slo, 8);
      case 0x13: { var a = this.AddrIndirectIndexed(out _); return this.RmwIllegal(a, this.Slo, 8); }

      // ── RLA (ROL then AND) ──
      case 0x27: return this.RmwIllegal(this.AddrZeroPage(), this.Rla, 5);
      case 0x37: return this.RmwIllegal(this.AddrZeroPageX(), this.Rla, 6);
      case 0x2F: return this.RmwIllegal(this.AddrAbsolute(), this.Rla, 6);
      case 0x3F: { var a = this.AddrAbsoluteX(out _); return this.RmwIllegal(a, this.Rla, 7); }
      case 0x3B: { var a = this.AddrAbsoluteY(out _); return this.RmwIllegal(a, this.Rla, 7); }
      case 0x23: return this.RmwIllegal(this.AddrIndexedIndirect(), this.Rla, 8);
      case 0x33: { var a = this.AddrIndirectIndexed(out _); return this.RmwIllegal(a, this.Rla, 8); }

      // ── SRE (LSR then EOR) ──
      case 0x47: return this.RmwIllegal(this.AddrZeroPage(), this.Sre, 5);
      case 0x57: return this.RmwIllegal(this.AddrZeroPageX(), this.Sre, 6);
      case 0x4F: return this.RmwIllegal(this.AddrAbsolute(), this.Sre, 6);
      case 0x5F: { var a = this.AddrAbsoluteX(out _); return this.RmwIllegal(a, this.Sre, 7); }
      case 0x5B: { var a = this.AddrAbsoluteY(out _); return this.RmwIllegal(a, this.Sre, 7); }
      case 0x43: return this.RmwIllegal(this.AddrIndexedIndirect(), this.Sre, 8);
      case 0x53: { var a = this.AddrIndirectIndexed(out _); return this.RmwIllegal(a, this.Sre, 8); }

      // ── RRA (ROR then ADC) ──
      case 0x67: return this.RmwIllegal(this.AddrZeroPage(), this.Rra, 5);
      case 0x77: return this.RmwIllegal(this.AddrZeroPageX(), this.Rra, 6);
      case 0x6F: return this.RmwIllegal(this.AddrAbsolute(), this.Rra, 6);
      case 0x7F: { var a = this.AddrAbsoluteX(out _); return this.RmwIllegal(a, this.Rra, 7); }
      case 0x7B: { var a = this.AddrAbsoluteY(out _); return this.RmwIllegal(a, this.Rra, 7); }
      case 0x63: return this.RmwIllegal(this.AddrIndexedIndirect(), this.Rra, 8);
      case 0x73: { var a = this.AddrIndirectIndexed(out _); return this.RmwIllegal(a, this.Rra, 8); }

      // ── immediate combiners ──
      case 0x0B: case 0x2B: // ANC #imm: AND then copy bit7 to carry
        this.And(this.Fetch()); this.SetFlag(Status.Carry, this.HasFlag(Status.Negative)); return 2;
      case 0x4B: // ALR/ASR #imm: AND then LSR A
        this.A &= this.Fetch(); this.A = this.Lsr(this.A); return 2;
      case 0x6B: return this.Arr();                       // ARR #imm
      case 0xCB: return this.Axs();                       // AXS/SBX #imm

      default:
        // Unstable/analog illegals (ANE/LXA/TAS/SHA/SHX/SHY) and KIL halts: treated as a
        // 2-cycle no-op. SID/NSF players do not use these; documented in the class summary.
        return 2;
    }
  }

  private long RmwIllegal(ushort addr, Func<byte, byte> op, long cycles) {
    var value = op(this.Read(addr));
    this.Write(addr, value);
    return cycles;
  }

  private byte Dcp(byte value) {
    var dec = (byte)(value - 1);
    this.Compare(this.A, dec);
    return dec;
  }

  private byte Isc(byte value) {
    var inc = (byte)(value + 1);
    this.Sbc(inc);
    return inc;
  }

  private byte Slo(byte value) {
    var shifted = this.Asl(value);
    this.Ora(shifted);
    return shifted;
  }

  private byte Rla(byte value) {
    var rotated = this.Rol(value);
    this.And(rotated);
    return rotated;
  }

  private byte Sre(byte value) {
    var shifted = this.Lsr(value);
    this.Eor(shifted);
    return shifted;
  }

  private byte Rra(byte value) {
    var rotated = this.Ror(value);
    this.Adc(rotated);
    return rotated;
  }

  private long Arr() {
    // AND #imm, then ROR A, with the documented special flag behaviour.
    var value = (byte)(this.A & this.Fetch());
    var carryIn = this.HasFlag(Status.Carry) ? 0x80 : 0;
    var result = (byte)((value >> 1) | carryIn);
    this.A = result;
    this.SetZN(result);
    this.SetFlag(Status.Carry, (result & 0x40) != 0);
    this.SetFlag(Status.Overflow, (((result >> 6) ^ (result >> 5)) & 1) != 0);
    return 2;
  }

  private long Axs() {
    // X = (A AND X) - imm, set flags like CMP (no borrow tracking beyond carry).
    var imm = this.Fetch();
    var tmp = (this.A & this.X) - imm;
    this.SetFlag(Status.Carry, (this.A & this.X) >= imm);
    this.X = (byte)tmp;
    this.SetZN(this.X);
    return 2;
  }
}
