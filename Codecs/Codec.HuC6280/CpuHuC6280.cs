#pragma warning disable CS1591
using Codec.Mos6502;

namespace Codec.HuC6280;

/// <summary>
/// A cycle-counting Hudson HuC6280 CPU core — the processor at the heart of the NEC PC Engine /
/// TurboGrafx-16. The HuC6280 is a superset of the WDC 65C02 (itself a cleaned-up 6502): it adds
/// the CMOS 65C02 opcodes (the <c>(zp)</c> addressing family, <c>STZ</c>, <c>BIT</c> immediate,
/// <c>TRB/TSB</c>, <c>PHX/PHY/PLX/PLY</c>, <c>BRA</c>, the bit-test branches <c>BBR/BBS</c> and
/// the bit set/reset <c>RMB/SMB</c>, and the fixed-up <c>JMP (abs,X)</c>/decimal-correct ADC/SBC)
/// and then a pile of Hudson-specific instructions on top:
/// <list type="bullet">
///   <item>the seven-cycle-per-byte <b>block-transfer</b> moves
///     <c>TII $73, TDD $C3, TIN $D3, TIA $E3, TAI $F3</c> (source/destination/length read from
///     three operand words; they consume the registers' worth of state and run to completion);</item>
///   <item>the <b>T-flag</b> (bit 5 of P) and <c>SET ($F4)</c> immediate, which redirect the next
///     ALU op's accumulator operand through a zero-page memory cell (the "memory operation"
///     mode);</item>
///   <item><c>TST ($83/$A3/$93/$B3)</c> test-against-immediate (sets Z/N/V without altering the
///     operand);</item>
///   <item>the bank mapper accessors <c>TAM ($53)</c> / <c>TMA ($43)</c> that load/store the
///     eight MPR (memory-paging) registers selected by a bitmask;</item>
///   <item>the speed switch <c>CSL ($54)</c> / <c>CSH ($D4)</c> (1.79 MHz vs 7.16 MHz);</item>
///   <item>the I/O port writes <c>ST0 ($03)</c> / <c>ST1 ($13)</c> / <c>ST2 ($23)</c> that latch
///     the VDC address and the PSG/VDC data ports;</item>
///   <item>register swaps <c>SXY ($02)</c>, <c>SAX ($22)</c>, <c>SAY ($42)</c>, plus <c>CLA/CLX/CLY</c>
///     ($62/$82/$C2) and the long branch <c>BSR ($44)</c>.</item>
/// </list>
/// <para>Opcode semantics and cycle counts follow the documented HuC6280 reverse-engineering
/// tables (the "HuC6280 CPU" opcode reference and Charles MacDonald's PC Engine notes) and the
/// Mednafen <c>huc6280.cpp</c> core. Memory is accessed exclusively through <see cref="IBus6502"/>;
/// the host bus is responsible for routing the 21-bit physical address that the MPR mapper would
/// otherwise produce — this core exposes the MPR registers (<see cref="Mpr"/>) so the bus can map
/// the logical 16-bit address through them.</para>
/// <para>Approximations: per-byte/-cycle bus timing is not pipelined (each instruction returns a
/// total cycle count); the block-transfer "alternate" source/destination pattern of TIA/TAI is
/// modelled by toggling which pointer increments, matching the documented behaviour. CSL/CSH are
/// tracked as a <see cref="HighSpeed"/> flag that the host may use to scale its clock; the core
/// itself does not change cycle counts.</para>
/// </summary>
public sealed class CpuHuC6280 {

  /// <summary>Processor status flag bits. Bit 5 is the HuC6280 <b>T</b> (memory-operation) flag.</summary>
  [Flags]
  public enum Status : byte {
    /// <summary>
    /// Specifies the carry option.
    /// </summary>
    Carry = 0x01,
    /// <summary>
    /// Specifies the zero option.
    /// </summary>
    Zero = 0x02,
    /// <summary>
    /// Specifies the interrupt option.
    /// </summary>
    Interrupt = 0x04,
    /// <summary>
    /// Specifies the decimal option.
    /// </summary>
    Decimal = 0x08,
    /// <summary>
    /// Specifies the break option.
    /// </summary>
    Break = 0x10,
    /// <summary>
    /// Specifies the memory option.
    /// </summary>
    Memory = 0x20, // T flag on the HuC6280 (the 6502 "unused" bit)
    /// <summary>
    /// Specifies the overflow option.
    /// </summary>
    Overflow = 0x40,
    /// <summary>
    /// Specifies the negative option.
    /// </summary>
    Negative = 0x80,
  }

  private readonly IBus6502 _bus;

  /// <summary>
  /// Provides the a value.
  /// </summary>
  public byte A;
  /// <summary>
  /// Provides the x value.
  /// </summary>
  public byte X;
  /// <summary>
  /// Provides the y value.
  /// </summary>
  public byte Y;
  /// <summary>
  /// Provides the sp value.
  /// </summary>
  public byte SP;
  /// <summary>
  /// Provides the pc value.
  /// </summary>
  public ushort PC;
  /// <summary>
  /// Provides the p value.
  /// </summary>
  public Status P;

  /// <summary>The eight MPR (memory-paging) registers — TAM/TMA load/store these; the host bus
  /// maps the logical address through them (logical bits 13-15 select the MPR, its value is the
  /// 8 KiB physical page).</summary>
  public readonly byte[] Mpr = new byte[8];

  /// <summary>True after <c>CSH</c>, false after <c>CSL</c>. The host may scale its clock by this
  /// (7.16 MHz vs 1.79 MHz); the core's cycle counts are speed-independent.</summary>
  public bool HighSpeed;

  /// <summary>
  /// Initializes a new instance of <see cref="CpuHuC6280"/>.
  /// </summary>
  public CpuHuC6280(IBus6502 bus) {
    this._bus = bus;
    this.Reset();
  }

  /// <summary>Power-on/reset: stack to $FF, interrupt-disable set, decimal clear, MPR cleared,
  /// PC loaded from the reset vector at $FFFE/$FFFF (the HuC6280's reset vector).</summary>
  public void Reset() {
    this.A = this.X = this.Y = 0;
    this.SP = 0xFF;
    this.P = Status.Interrupt;
    this.HighSpeed = false;
    Array.Clear(this.Mpr);
    this.PC = (ushort)(this._bus.Read(0xFFFE) | (this._bus.Read(0xFFFF) << 8));
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

  private void Push(byte value) => this.Write((ushort)(0x2100 | this.SP--), value);
  private byte Pop() => this.Read((ushort)(0x2100 | ++this.SP));

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
  /// Calls into a subroutine at <paramref name="address"/> using the player convention: a
  /// sentinel return address is pushed so the matching <c>RTS</c> lands on a known PC, at which
  /// point execution stops. Returns the cycles consumed (capped). Used by the HES player to
  /// invoke the tune's init and play routines.
  /// </summary>
  public long RunUntilRts(ushort address, long maxCycles) {
    const ushort sentinel = 0x0000;
    var targetStack = this.SP;
    this.PushWord(sentinel);
    this.PC = address;

    long cycles = 0;
    while (cycles < maxCycles) {
      if (this.PC == sentinel + 1 && this.SP == targetStack)
        break;
      cycles += this.Step();
    }
    return cycles;
  }

  // ── addressing-mode operand resolution ──────────────────────────────────────
  //
  // The HuC6280 zero page is fixed at $2000 (MPR maps it there), but for the core the IBus6502
  // host is responsible for that mapping; from the CPU's perspective the zero page is logical
  // $0000-$00FF and the stack is $0100-$01FF, with the host's MPR pointing both at physical RAM.

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
    var ptr = (byte)(this.Fetch() + this.X);
    var lo = this.Read(ptr);
    var hi = this.Read((byte)(ptr + 1));
    return (ushort)(lo | (hi << 8));
  }

  private ushort AddrIndirectIndexed(out bool crossed) {
    var ptr = this.Fetch();
    var lo = this.Read(ptr);
    var hi = this.Read((byte)(ptr + 1));
    var baseAddr = (ushort)(lo | (hi << 8));
    var addr = (ushort)(baseAddr + this.Y);
    crossed = (baseAddr & 0xFF00) != (addr & 0xFF00);
    return addr;
  }

  // 65C02 (zp) — zero-page indirect, no index.
  private ushort AddrIndirect() {
    var ptr = this.Fetch();
    var lo = this.Read(ptr);
    var hi = this.Read((byte)(ptr + 1));
    return (ushort)(lo | (hi << 8));
  }

  // ── ALU primitives ──────────────────────────────────────────────────────────

  private void Adc(byte value) {
    if (this.HasFlag(Status.Decimal)) {
      // 65C02 decimal ADC: flags reflect the corrected result (unlike NMOS).
      var c0 = this.HasFlag(Status.Carry) ? 1 : 0;
      var lo = (this.A & 0x0F) + (value & 0x0F) + c0;
      var hi = (this.A >> 4) + (value >> 4);
      if (lo > 9) { lo += 6; hi++; }
      var carry = hi > 9;
      if (carry) hi += 6;
      var result = (byte)((lo & 0x0F) | ((hi & 0x0F) << 4));
      this.SetFlag(Status.Carry, carry);
      this.A = result;
      this.SetZN(result);
      return;
    }
    var c = this.HasFlag(Status.Carry) ? 1 : 0;
    var sum = this.A + value + c;
    var r = (byte)sum;
    this.SetFlag(Status.Carry, sum > 0xFF);
    this.SetFlag(Status.Overflow, ((this.A ^ r) & (value ^ r) & 0x80) != 0);
    this.A = r;
    this.SetZN(r);
  }

  private void Sbc(byte value) {
    if (this.HasFlag(Status.Decimal)) {
      var c0 = this.HasFlag(Status.Carry) ? 1 : 0;
      var lo = (this.A & 0x0F) - (value & 0x0F) - (1 - c0);
      var hi = (this.A >> 4) - (value >> 4);
      if ((lo & 0x10) != 0) { lo -= 6; hi--; }
      if ((hi & 0x10) != 0) hi -= 6;
      var binDiff = this.A - value - (1 - c0);
      var result = (byte)((lo & 0x0F) | ((hi & 0x0F) << 4));
      this.SetFlag(Status.Carry, binDiff >= 0);
      this.A = result;
      this.SetZN(result);
      return;
    }
    var c = this.HasFlag(Status.Carry) ? 1 : 0;
    var diff = this.A - value - (1 - c);
    var r = (byte)diff;
    this.SetFlag(Status.Carry, diff >= 0);
    this.SetFlag(Status.Overflow, ((this.A ^ value) & (this.A ^ r) & 0x80) != 0);
    this.A = r;
    this.SetZN(r);
  }

  private void Compare(byte register, byte value) {
    var diff = register - value;
    this.SetFlag(Status.Carry, register >= value);
    this.SetZN((byte)diff);
  }

  // The HuC6280 T (memory) flag redirects the accumulator operand of the immediately-following
  // ALU instruction through a zero-page cell pointed at by X. We implement the common case where
  // the next op is an immediate/loaded ALU op by routing reads/writes; here And/Ora/Eor/Adc act
  // on A unless a T-target is staged.
  private void And(byte value) {
    if (this._tTarget is { } addr) { var v = (byte)(this.Read(addr) & value); this.Write(addr, v); this.SetZN(v); this._tTarget = null; return; }
    this.A &= value; this.SetZN(this.A);
  }
  private void Ora(byte value) {
    if (this._tTarget is { } addr) { var v = (byte)(this.Read(addr) | value); this.Write(addr, v); this.SetZN(v); this._tTarget = null; return; }
    this.A |= value; this.SetZN(this.A);
  }
  private void Eor(byte value) {
    if (this._tTarget is { } addr) { var v = (byte)(this.Read(addr) ^ value); this.Write(addr, v); this.SetZN(v); this._tTarget = null; return; }
    this.A ^= value; this.SetZN(this.A);
  }

  private ushort? _tTarget;

  private void Bit(byte value) {
    this.SetFlag(Status.Zero, (this.A & value) == 0);
    this.SetFlag(Status.Negative, (value & 0x80) != 0);
    this.SetFlag(Status.Overflow, (value & 0x40) != 0);
  }

  // BIT immediate (65C02): only Z is affected.
  private void BitImmediate(byte value) => this.SetFlag(Status.Zero, (this.A & value) == 0);

  // TST imm,addr (HuC6280): AND immediate against memory, set Z/N/V; operand unchanged.
  private void Tst(byte imm, byte mem) {
    this.SetFlag(Status.Zero, (imm & mem) == 0);
    this.SetFlag(Status.Negative, (mem & 0x80) != 0);
    this.SetFlag(Status.Overflow, (mem & 0x40) != 0);
  }

  private byte Asl(byte value) { this.SetFlag(Status.Carry, (value & 0x80) != 0); var r = (byte)(value << 1); this.SetZN(r); return r; }
  private byte Lsr(byte value) { this.SetFlag(Status.Carry, (value & 0x01) != 0); var r = (byte)(value >> 1); this.SetZN(r); return r; }
  private byte Rol(byte value) { var ci = this.HasFlag(Status.Carry) ? 1 : 0; this.SetFlag(Status.Carry, (value & 0x80) != 0); var r = (byte)((value << 1) | ci); this.SetZN(r); return r; }
  private byte Ror(byte value) { var ci = this.HasFlag(Status.Carry) ? 0x80 : 0; this.SetFlag(Status.Carry, (value & 0x01) != 0); var r = (byte)((value >> 1) | ci); this.SetZN(r); return r; }

  // TRB / TSB (65C02): test-and-reset / test-and-set bits, Z from A AND memory.
  private byte Trb(byte value) { this.SetFlag(Status.Zero, (this.A & value) == 0); return (byte)(value & ~this.A); }
  private byte Tsb(byte value) { this.SetFlag(Status.Zero, (this.A & value) == 0); return (byte)(value | this.A); }

  private long Branch(bool condition) {
    var offset = (sbyte)this.Fetch();
    if (!condition)
      return 2;
    this.PC = (ushort)(this.PC + offset);
    return 4;
  }

  // ── instruction dispatch ─────────────────────────────────────────────────────

  /// <summary>Executes one instruction and returns the clock cycles it consumed.</summary>
  public long Step() {
    var opcode = this.Fetch();
    switch (opcode) {
      // ── LDA ──
      case 0xA9: this.A = this.Fetch(); this.SetZN(this.A); return 2;
      case 0xA5: this.A = this.Read(this.AddrZeroPage()); this.SetZN(this.A); return 4;
      case 0xB5: this.A = this.Read(this.AddrZeroPageX()); this.SetZN(this.A); return 4;
      case 0xAD: this.A = this.Read(this.AddrAbsolute()); this.SetZN(this.A); return 5;
      case 0xBD: { this.A = this.Read(this.AddrAbsoluteX(out _)); this.SetZN(this.A); return 5; }
      case 0xB9: { this.A = this.Read(this.AddrAbsoluteY(out _)); this.SetZN(this.A); return 5; }
      case 0xA1: this.A = this.Read(this.AddrIndexedIndirect()); this.SetZN(this.A); return 7;
      case 0xB1: { this.A = this.Read(this.AddrIndirectIndexed(out _)); this.SetZN(this.A); return 7; }
      case 0xB2: this.A = this.Read(this.AddrIndirect()); this.SetZN(this.A); return 7; // LDA (zp)

      // ── LDX ──
      case 0xA2: this.X = this.Fetch(); this.SetZN(this.X); return 2;
      case 0xA6: this.X = this.Read(this.AddrZeroPage()); this.SetZN(this.X); return 4;
      case 0xB6: this.X = this.Read(this.AddrZeroPageY()); this.SetZN(this.X); return 4;
      case 0xAE: this.X = this.Read(this.AddrAbsolute()); this.SetZN(this.X); return 5;
      case 0xBE: { this.X = this.Read(this.AddrAbsoluteY(out _)); this.SetZN(this.X); return 5; }

      // ── LDY ──
      case 0xA0: this.Y = this.Fetch(); this.SetZN(this.Y); return 2;
      case 0xA4: this.Y = this.Read(this.AddrZeroPage()); this.SetZN(this.Y); return 4;
      case 0xB4: this.Y = this.Read(this.AddrZeroPageX()); this.SetZN(this.Y); return 4;
      case 0xAC: this.Y = this.Read(this.AddrAbsolute()); this.SetZN(this.Y); return 5;
      case 0xBC: { this.Y = this.Read(this.AddrAbsoluteX(out _)); this.SetZN(this.Y); return 5; }

      // ── STA ──
      case 0x85: this.Write(this.AddrZeroPage(), this.A); return 4;
      case 0x95: this.Write(this.AddrZeroPageX(), this.A); return 4;
      case 0x8D: this.Write(this.AddrAbsolute(), this.A); return 5;
      case 0x9D: { var a = this.AddrAbsoluteX(out _); this.Write(a, this.A); return 5; }
      case 0x99: { var a = this.AddrAbsoluteY(out _); this.Write(a, this.A); return 5; }
      case 0x81: this.Write(this.AddrIndexedIndirect(), this.A); return 7;
      case 0x91: { var a = this.AddrIndirectIndexed(out _); this.Write(a, this.A); return 7; }
      case 0x92: this.Write(this.AddrIndirect(), this.A); return 7; // STA (zp)

      // ── STX / STY ──
      case 0x86: this.Write(this.AddrZeroPage(), this.X); return 4;
      case 0x96: this.Write(this.AddrZeroPageY(), this.X); return 4;
      case 0x8E: this.Write(this.AddrAbsolute(), this.X); return 5;
      case 0x84: this.Write(this.AddrZeroPage(), this.Y); return 4;
      case 0x94: this.Write(this.AddrZeroPageX(), this.Y); return 4;
      case 0x8C: this.Write(this.AddrAbsolute(), this.Y); return 5;

      // ── STZ (65C02) ──
      case 0x64: this.Write(this.AddrZeroPage(), 0); return 4;
      case 0x74: this.Write(this.AddrZeroPageX(), 0); return 4;
      case 0x9C: this.Write(this.AddrAbsolute(), 0); return 5;
      case 0x9E: { var a = this.AddrAbsoluteX(out _); this.Write(a, 0); return 5; }

      // ── transfers ──
      case 0xAA: this.X = this.A; this.SetZN(this.X); return 2; // TAX
      case 0xA8: this.Y = this.A; this.SetZN(this.Y); return 2; // TAY
      case 0x8A: this.A = this.X; this.SetZN(this.A); return 2; // TXA
      case 0x98: this.A = this.Y; this.SetZN(this.A); return 2; // TYA
      case 0xBA: this.X = this.SP; this.SetZN(this.X); return 2; // TSX
      case 0x9A: this.SP = this.X; return 2;                     // TXS

      // ── register swaps & clears (HuC6280) ──
      case 0x02: (this.X, this.Y) = (this.Y, this.X); return 3;           // SXY
      case 0x22: (this.A, this.X) = (this.X, this.A); return 3;           // SAX
      case 0x42: (this.A, this.Y) = (this.Y, this.A); return 3;           // SAY
      case 0x62: this.A = 0; return 2;                                    // CLA
      case 0x82: this.X = 0; return 2;                                    // CLX
      case 0xC2: this.Y = 0; return 2;                                    // CLY

      // ── stack ──
      case 0x48: this.Push(this.A); return 3;                                          // PHA
      case 0x68: this.A = this.Pop(); this.SetZN(this.A); return 4;                     // PLA
      case 0x08: this.Push((byte)(this.P | Status.Break)); return 3;                   // PHP
      case 0x28: this.P = (Status)(this.Pop() & ~(byte)Status.Break); return 4;        // PLP
      case 0xDA: this.Push(this.X); return 3;                                          // PHX
      case 0xFA: this.X = this.Pop(); this.SetZN(this.X); return 4;                     // PLX
      case 0x5A: this.Push(this.Y); return 3;                                          // PHY
      case 0x7A: this.Y = this.Pop(); this.SetZN(this.Y); return 4;                     // PLY

      // ── logic (immediate / memory) ──
      case 0x29: this.And(this.Fetch()); return 2;
      case 0x25: this.And(this.Read(this.AddrZeroPage())); return 4;
      case 0x35: this.And(this.Read(this.AddrZeroPageX())); return 4;
      case 0x2D: this.And(this.Read(this.AddrAbsolute())); return 5;
      case 0x3D: { this.And(this.Read(this.AddrAbsoluteX(out _))); return 5; }
      case 0x39: { this.And(this.Read(this.AddrAbsoluteY(out _))); return 5; }
      case 0x21: this.And(this.Read(this.AddrIndexedIndirect())); return 7;
      case 0x31: { this.And(this.Read(this.AddrIndirectIndexed(out _))); return 7; }
      case 0x32: this.And(this.Read(this.AddrIndirect())); return 7;

      case 0x09: this.Ora(this.Fetch()); return 2;
      case 0x05: this.Ora(this.Read(this.AddrZeroPage())); return 4;
      case 0x15: this.Ora(this.Read(this.AddrZeroPageX())); return 4;
      case 0x0D: this.Ora(this.Read(this.AddrAbsolute())); return 5;
      case 0x1D: { this.Ora(this.Read(this.AddrAbsoluteX(out _))); return 5; }
      case 0x19: { this.Ora(this.Read(this.AddrAbsoluteY(out _))); return 5; }
      case 0x01: this.Ora(this.Read(this.AddrIndexedIndirect())); return 7;
      case 0x11: { this.Ora(this.Read(this.AddrIndirectIndexed(out _))); return 7; }
      case 0x12: this.Ora(this.Read(this.AddrIndirect())); return 7;

      case 0x49: this.Eor(this.Fetch()); return 2;
      case 0x45: this.Eor(this.Read(this.AddrZeroPage())); return 4;
      case 0x55: this.Eor(this.Read(this.AddrZeroPageX())); return 4;
      case 0x4D: this.Eor(this.Read(this.AddrAbsolute())); return 5;
      case 0x5D: { this.Eor(this.Read(this.AddrAbsoluteX(out _))); return 5; }
      case 0x59: { this.Eor(this.Read(this.AddrAbsoluteY(out _))); return 5; }
      case 0x41: this.Eor(this.Read(this.AddrIndexedIndirect())); return 7;
      case 0x51: { this.Eor(this.Read(this.AddrIndirectIndexed(out _))); return 7; }
      case 0x52: this.Eor(this.Read(this.AddrIndirect())); return 7;

      // ── BIT ──
      case 0x89: this.BitImmediate(this.Fetch()); return 2;     // BIT # (65C02)
      case 0x24: this.Bit(this.Read(this.AddrZeroPage())); return 4;
      case 0x2C: this.Bit(this.Read(this.AddrAbsolute())); return 5;
      case 0x34: this.Bit(this.Read(this.AddrZeroPageX())); return 4;
      case 0x3C: { this.Bit(this.Read(this.AddrAbsoluteX(out _))); return 5; }

      // ── ADC ──
      case 0x69: this.Adc(this.Fetch()); return 2;
      case 0x65: this.Adc(this.Read(this.AddrZeroPage())); return 4;
      case 0x75: this.Adc(this.Read(this.AddrZeroPageX())); return 4;
      case 0x6D: this.Adc(this.Read(this.AddrAbsolute())); return 5;
      case 0x7D: { this.Adc(this.Read(this.AddrAbsoluteX(out _))); return 5; }
      case 0x79: { this.Adc(this.Read(this.AddrAbsoluteY(out _))); return 5; }
      case 0x61: this.Adc(this.Read(this.AddrIndexedIndirect())); return 7;
      case 0x71: { this.Adc(this.Read(this.AddrIndirectIndexed(out _))); return 7; }
      case 0x72: this.Adc(this.Read(this.AddrIndirect())); return 7;

      // ── SBC ──
      case 0xE9: this.Sbc(this.Fetch()); return 2;
      case 0xE5: this.Sbc(this.Read(this.AddrZeroPage())); return 4;
      case 0xF5: this.Sbc(this.Read(this.AddrZeroPageX())); return 4;
      case 0xED: this.Sbc(this.Read(this.AddrAbsolute())); return 5;
      case 0xFD: { this.Sbc(this.Read(this.AddrAbsoluteX(out _))); return 5; }
      case 0xF9: { this.Sbc(this.Read(this.AddrAbsoluteY(out _))); return 5; }
      case 0xE1: this.Sbc(this.Read(this.AddrIndexedIndirect())); return 7;
      case 0xF1: { this.Sbc(this.Read(this.AddrIndirectIndexed(out _))); return 7; }
      case 0xF2: this.Sbc(this.Read(this.AddrIndirect())); return 7;

      // ── CMP ──
      case 0xC9: this.Compare(this.A, this.Fetch()); return 2;
      case 0xC5: this.Compare(this.A, this.Read(this.AddrZeroPage())); return 4;
      case 0xD5: this.Compare(this.A, this.Read(this.AddrZeroPageX())); return 4;
      case 0xCD: this.Compare(this.A, this.Read(this.AddrAbsolute())); return 5;
      case 0xDD: { this.Compare(this.A, this.Read(this.AddrAbsoluteX(out _))); return 5; }
      case 0xD9: { this.Compare(this.A, this.Read(this.AddrAbsoluteY(out _))); return 5; }
      case 0xC1: this.Compare(this.A, this.Read(this.AddrIndexedIndirect())); return 7;
      case 0xD1: { this.Compare(this.A, this.Read(this.AddrIndirectIndexed(out _))); return 7; }
      case 0xD2: this.Compare(this.A, this.Read(this.AddrIndirect())); return 7;

      // ── CPX / CPY ──
      case 0xE0: this.Compare(this.X, this.Fetch()); return 2;
      case 0xE4: this.Compare(this.X, this.Read(this.AddrZeroPage())); return 4;
      case 0xEC: this.Compare(this.X, this.Read(this.AddrAbsolute())); return 5;
      case 0xC0: this.Compare(this.Y, this.Fetch()); return 2;
      case 0xC4: this.Compare(this.Y, this.Read(this.AddrZeroPage())); return 4;
      case 0xCC: this.Compare(this.Y, this.Read(this.AddrAbsolute())); return 5;

      // ── INC / DEC (memory) ──
      case 0xE6: return this.Rmw(this.AddrZeroPage(), this.IncFlagged, 6);
      case 0xF6: return this.Rmw(this.AddrZeroPageX(), this.IncFlagged, 6);
      case 0xEE: return this.Rmw(this.AddrAbsolute(), this.IncFlagged, 7);
      case 0xFE: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.IncFlagged, 7); }
      case 0xC6: return this.Rmw(this.AddrZeroPage(), this.DecFlagged, 6);
      case 0xD6: return this.Rmw(this.AddrZeroPageX(), this.DecFlagged, 6);
      case 0xCE: return this.Rmw(this.AddrAbsolute(), this.DecFlagged, 7);
      case 0xDE: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.DecFlagged, 7); }
      case 0x1A: this.A++; this.SetZN(this.A); return 2;   // INC A (65C02)
      case 0x3A: this.A--; this.SetZN(this.A); return 2;   // DEC A (65C02)

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

      // ── ASL/LSR/ROL/ROR (memory) ──
      case 0x06: return this.Rmw(this.AddrZeroPage(), this.Asl, 6);
      case 0x16: return this.Rmw(this.AddrZeroPageX(), this.Asl, 6);
      case 0x0E: return this.Rmw(this.AddrAbsolute(), this.Asl, 7);
      case 0x1E: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.Asl, 7); }
      case 0x46: return this.Rmw(this.AddrZeroPage(), this.Lsr, 6);
      case 0x56: return this.Rmw(this.AddrZeroPageX(), this.Lsr, 6);
      case 0x4E: return this.Rmw(this.AddrAbsolute(), this.Lsr, 7);
      case 0x5E: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.Lsr, 7); }
      case 0x26: return this.Rmw(this.AddrZeroPage(), this.Rol, 6);
      case 0x36: return this.Rmw(this.AddrZeroPageX(), this.Rol, 6);
      case 0x2E: return this.Rmw(this.AddrAbsolute(), this.Rol, 7);
      case 0x3E: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.Rol, 7); }
      case 0x66: return this.Rmw(this.AddrZeroPage(), this.Ror, 6);
      case 0x76: return this.Rmw(this.AddrZeroPageX(), this.Ror, 6);
      case 0x6E: return this.Rmw(this.AddrAbsolute(), this.Ror, 7);
      case 0x7E: { var a = this.AddrAbsoluteX(out _); return this.Rmw(a, this.Ror, 7); }

      // ── TRB / TSB (65C02) ──
      case 0x14: return this.Rmw(this.AddrZeroPage(), this.Trb, 6);
      case 0x1C: return this.Rmw(this.AddrAbsolute(), this.Trb, 7);
      case 0x04: return this.Rmw(this.AddrZeroPage(), this.Tsb, 6);
      case 0x0C: return this.Rmw(this.AddrAbsolute(), this.Tsb, 7);

      // ── jumps / subroutines ──
      case 0x4C: this.PC = this.AddrAbsolute(); return 4;                 // JMP abs
      case 0x6C: return this.JmpIndirect();                              // JMP (ind)
      case 0x7C: return this.JmpIndirectX();                             // JMP (abs,X) (65C02)
      case 0x20: return this.Jsr();                                       // JSR
      case 0x60: this.PC = (ushort)(this.PopWord() + 1); return 7;        // RTS
      case 0x40: return this.Rti();                                       // RTI
      case 0x00: return this.Brk();                                       // BRK
      case 0x44: return this.Bsr();                                       // BSR (HuC6280)

      // ── branches ──
      case 0x80: return this.Branch(true);                            // BRA (65C02)
      case 0x10: return this.Branch(!this.HasFlag(Status.Negative));  // BPL
      case 0x30: return this.Branch(this.HasFlag(Status.Negative));   // BMI
      case 0x50: return this.Branch(!this.HasFlag(Status.Overflow));  // BVC
      case 0x70: return this.Branch(this.HasFlag(Status.Overflow));   // BVS
      case 0x90: return this.Branch(!this.HasFlag(Status.Carry));     // BCC
      case 0xB0: return this.Branch(this.HasFlag(Status.Carry));      // BCS
      case 0xD0: return this.Branch(!this.HasFlag(Status.Zero));      // BNE
      case 0xF0: return this.Branch(this.HasFlag(Status.Zero));       // BEQ

      // ── flag set/clear ──
      case 0x18: this.SetFlag(Status.Carry, false); return 2;     // CLC
      case 0x38: this.SetFlag(Status.Carry, true); return 2;      // SEC
      case 0x58: this.SetFlag(Status.Interrupt, false); return 2; // CLI
      case 0x78: this.SetFlag(Status.Interrupt, true); return 2;  // SEI
      case 0xB8: this.SetFlag(Status.Overflow, false); return 2;  // CLV
      case 0xD8: this.SetFlag(Status.Decimal, false); return 2;   // CLD
      case 0xF8: this.SetFlag(Status.Decimal, true); return 2;    // SED

      // ── HuC6280 control ──
      case 0x54: this.HighSpeed = false; return 3;   // CSL (1.79 MHz)
      case 0xD4: this.HighSpeed = true; return 3;    // CSH (7.16 MHz)
      case 0xF4: return this.SetTFlag();             // SET (stage T-flag operation)

      // ── HuC6280 I/O port writes ──
      case 0x03: this.Write(0x0000, this.Fetch()); return 4;   // ST0 → VDC address ($0000 IO)
      case 0x13: this.Write(0x0002, this.Fetch()); return 4;   // ST1 → VDC/PSG data low
      case 0x23: this.Write(0x0003, this.Fetch()); return 4;   // ST2 → VDC/PSG data high

      // ── HuC6280 MPR mapper ──
      case 0x43: return this.Tma();   // TMA: A ← MPR[selected by mask]
      case 0x53: return this.Tam();   // TAM: MPR[mask bits] ← A

      // ── HuC6280 TST ──
      case 0x83: { var imm = this.Fetch(); var a = this.AddrZeroPage(); this.Tst(imm, this.Read(a)); return 7; }
      case 0xA3: { var imm = this.Fetch(); var a = this.AddrAbsolute(); this.Tst(imm, this.Read(a)); return 8; }
      case 0x93: { var imm = this.Fetch(); var a = this.AddrZeroPageX(); this.Tst(imm, this.Read(a)); return 7; }
      case 0xB3: { var imm = this.Fetch(); var a = this.AddrAbsoluteX(out _); this.Tst(imm, this.Read(a)); return 8; }

      // ── HuC6280 block transfers ──
      case 0x73: return this.BlockTransfer(BlockMode.Tii); // TII: src+, dst+
      case 0xC3: return this.BlockTransfer(BlockMode.Tdd); // TDD: src-, dst-
      case 0xD3: return this.BlockTransfer(BlockMode.Tin); // TIN: src+, dst fixed
      case 0xE3: return this.BlockTransfer(BlockMode.Tia); // TIA: src+, dst alternates
      case 0xF3: return this.BlockTransfer(BlockMode.Tai); // TAI: src alternates, dst+

      // ── RMB/SMB/BBR/BBS (Rockwell/WDC bit ops, present on the HuC6280) ──
      case 0x07: case 0x17: case 0x27: case 0x37:
      case 0x47: case 0x57: case 0x67: case 0x77:
        return this.Rmb(opcode);
      case 0x87: case 0x97: case 0xA7: case 0xB7:
      case 0xC7: case 0xD7: case 0xE7: case 0xF7:
        return this.Smb(opcode);
      case 0x0F: case 0x1F: case 0x2F: case 0x3F:
      case 0x4F: case 0x5F: case 0x6F: case 0x7F:
        return this.Bbr(opcode);
      case 0x8F: case 0x9F: case 0xAF: case 0xBF:
      case 0xCF: case 0xDF: case 0xEF: case 0xFF:
        return this.Bbs(opcode);

      // ── NOP (official) ──
      case 0xEA: return 2;

      default:
        // Unmapped opcode — treat as a 2-cycle NOP (the HuC6280 has no documented illegals).
        return 2;
    }
  }

  // ── HuC6280 helper opcodes ──────────────────────────────────────────────────

  private long SetTFlag() {
    // SET: the next ALU instruction operates on the zero-page cell pointed at by X instead of A.
    this.SetFlag(Status.Memory, true);
    this._tTarget = this.X;
    return 2;
  }

  private long Tam() {
    // TAM #mask: copy A into every MPR register whose bit is set in the mask.
    var mask = this.Fetch();
    for (var i = 0; i < 8; ++i)
      if ((mask & (1 << i)) != 0)
        this.Mpr[i] = this.A;
    return 5;
  }

  private long Tma() {
    // TMA #mask: A ← the (lowest-set-bit) selected MPR register's value.
    var mask = this.Fetch();
    for (var i = 0; i < 8; ++i)
      if ((mask & (1 << i)) != 0) {
        this.A = this.Mpr[i];
        break;
      }
    return 4;
  }

  private enum BlockMode { Tii, Tdd, Tin, Tia, Tai }

  private long BlockTransfer(BlockMode mode) {
    // Three operand words follow the opcode: source, destination, length.
    var src = this.FetchWord();
    var dst = this.FetchWord();
    var len = this.FetchWord();
    var count = len == 0 ? 0x10000 : len;

    // TIA/TAI "alternate" the fixed pointer between offset 0 and +1 across successive bytes
    // (the documented ping-pong: dst, dst+1, dst, dst+1, … for TIA; same on the source for TAI).
    var alt = 0;
    for (var i = 0; i < count; ++i) {
      switch (mode) {
        case BlockMode.Tii: this.Write(dst, this.Read(src)); src++; dst++; break;
        case BlockMode.Tdd: this.Write(dst, this.Read(src)); src--; dst--; break;
        case BlockMode.Tin: this.Write(dst, this.Read(src)); src++; break;           // dst fixed
        case BlockMode.Tia: this.Write((ushort)(dst + alt), this.Read(src)); src++; alt ^= 1; break;
        case BlockMode.Tai: this.Write(dst, this.Read((ushort)(src + alt))); dst++; alt ^= 1; break;
      }
    }
    // 17 base cycles + 6 per byte (documented HuC6280 block-move timing).
    return 17 + 6L * count;
  }

  private long Rmw(ushort addr, Func<byte, byte> op, long cycles) {
    var value = op(this.Read(addr));
    this.Write(addr, value);
    return cycles;
  }

  private byte IncFlagged(byte value) { var r = (byte)(value + 1); this.SetZN(r); return r; }
  private byte DecFlagged(byte value) { var r = (byte)(value - 1); this.SetZN(r); return r; }

  private long JmpIndirect() {
    // 65C02: no page-boundary bug — the high byte fetch wraps correctly.
    var ptr = this.FetchWord();
    var lo = this.Read(ptr);
    var hi = this.Read((ushort)(ptr + 1));
    this.PC = (ushort)(lo | (hi << 8));
    return 7;
  }

  private long JmpIndirectX() {
    var ptr = (ushort)(this.FetchWord() + this.X);
    var lo = this.Read(ptr);
    var hi = this.Read((ushort)(ptr + 1));
    this.PC = (ushort)(lo | (hi << 8));
    return 7;
  }

  private long Jsr() {
    var target = this.FetchWord();
    this.PushWord((ushort)(this.PC - 1));
    this.PC = target;
    return 7;
  }

  private long Bsr() {
    // Branch to subroutine: relative offset, pushes return address like JSR.
    var offset = (sbyte)this.Fetch();
    this.PushWord((ushort)(this.PC - 1));
    this.PC = (ushort)(this.PC + offset);
    return 8;
  }

  private long Rti() {
    this.P = (Status)(this.Pop() & ~(byte)Status.Break);
    this.PC = this.PopWord();
    return 7;
  }

  private long Brk() {
    this.PC++;
    this.PushWord(this.PC);
    this.Push((byte)(this.P | Status.Break));
    this.SetFlag(Status.Interrupt, true);
    this.SetFlag(Status.Decimal, false);
    this.PC = (ushort)(this.Read(0xFFF6) | (this.Read(0xFFF7) << 8)); // HuC6280 BRK vector
    return 8;
  }

  // ── Rockwell bit ops (RMB/SMB/BBR/BBS) ──

  private long Rmb(byte opcode) {
    var bit = (opcode >> 4) & 0x07;
    var addr = this.AddrZeroPage();
    this.Write(addr, (byte)(this.Read(addr) & ~(1 << bit)));
    return 7;
  }

  private long Smb(byte opcode) {
    var bit = (opcode >> 4) & 0x07;
    var addr = this.AddrZeroPage();
    this.Write(addr, (byte)(this.Read(addr) | (1 << bit)));
    return 7;
  }

  private long Bbr(byte opcode) {
    var bit = (opcode >> 4) & 0x07;
    var value = this.Read(this.AddrZeroPage());
    var offset = (sbyte)this.Fetch();
    if ((value & (1 << bit)) == 0)
      this.PC = (ushort)(this.PC + offset);
    return 6;
  }

  private long Bbs(byte opcode) {
    var bit = (opcode >> 4) & 0x07;
    var value = this.Read(this.AddrZeroPage());
    var offset = (sbyte)this.Fetch();
    if ((value & (1 << bit)) != 0)
      this.PC = (ushort)(this.PC + offset);
    return 6;
  }
}
