#pragma warning disable CS1591
namespace Codec.Spc700;

/// <summary>
/// A cycle-counting interpreter for the Sony SPC700 (the SNES APU's 8-bit CPU). The core
/// owns the 64&#160;KB <see cref="Apu"/> address space and executes one instruction per
/// <see cref="Step"/> call, returning the number of master cycles consumed (the SPC700 runs
/// at ~1.024&#160;MHz). Registers are the standard SPC700 set: <c>PC</c>, <c>A</c>, <c>X</c>,
/// <c>Y</c>, <c>SP</c> (stack lives in page&#160;1, <c>$0100-$01FF</c>) and the processor
/// status word <c>PSW</c>.
/// <para>Memory side effects (DSP register access, timers, the CONTROL register, I/O ports
/// and the IPL ROM) are delegated to <see cref="Apu"/>; the CPU only deals with bytes.</para>
/// The instruction timings follow the documented per-opcode cycle counts (Anomie's SPC700
/// reference); a handful of rarely used opcodes share the canonical timing of their family.
/// </summary>
public sealed class Spc700Cpu {

  // PSW flag bits.
  private const byte FlagC = 0x01; // carry
  private const byte FlagZ = 0x02; // zero
  private const byte FlagI = 0x04; // interrupt enable (unused by player, tracked for fidelity)
  private const byte FlagH = 0x08; // half-carry
  private const byte FlagB = 0x10; // break
  private const byte FlagP = 0x20; // direct-page select ($00xx vs $01xx)
  private const byte FlagV = 0x40; // overflow
  private const byte FlagN = 0x80; // negative

  private readonly Apu _apu;

  /// <summary>
  /// Provides the pc value.
  /// </summary>
  public ushort Pc;
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
  public byte Sp;
  /// <summary>
  /// Provides the psw value.
  /// </summary>
  public byte Psw;

  /// <summary>
  /// Initializes a new instance of <see cref="Spc700Cpu"/>.
  /// </summary>
  public Spc700Cpu(Apu apu) => this._apu = apu;

  // ── register helpers ────────────────────────────────────────────────────────

  /// <summary>The YA 16-bit pseudo-register (<c>Y</c> high, <c>A</c> low).</summary>
  private ushort Ya {
    get => (ushort)((this.Y << 8) | this.A);
    set { this.A = (byte)value; this.Y = (byte)(value >> 8); }
  }

  private bool GetFlag(byte mask) => (this.Psw & mask) != 0;

  private void SetFlag(byte mask, bool value) {
    if (value)
      this.Psw |= mask;
    else
      this.Psw = (byte)(this.Psw & ~mask);
  }

  private void SetNz(byte value) {
    this.SetFlag(FlagZ, value == 0);
    this.SetFlag(FlagN, (value & 0x80) != 0);
  }

  // ── memory access ────────────────────────────────────────────────────────────

  private byte Read(ushort address) => this._apu.Read(address);
  private void Write(ushort address, byte value) => this._apu.Write(address, value);

  private byte ReadPc() => this.Read(this.Pc++);

  private ushort ReadPc16() {
    var lo = this.ReadPc();
    var hi = this.ReadPc();
    return (ushort)(lo | (hi << 8));
  }

  /// <summary>Direct-page address for an offset, honouring the P flag (page $00 or $01).</summary>
  private ushort Dp(byte offset) => (ushort)(((this.Psw & FlagP) != 0 ? 0x0100 : 0x0000) | offset);

  private byte ReadDp(byte offset) => this.Read(this.Dp(offset));
  private void WriteDp(byte offset, byte value) => this.Write(this.Dp(offset), value);

  // ── stack ─────────────────────────────────────────────────────────────────────

  private void Push(byte value) => this.Write((ushort)(0x0100 | this.Sp--), value);
  private byte Pop() => this.Read((ushort)(0x0100 | ++this.Sp));

  private void Push16(ushort value) {
    this.Push((byte)(value >> 8));
    this.Push((byte)value);
  }

  private ushort Pop16() {
    var lo = this.Pop();
    var hi = this.Pop();
    return (ushort)(lo | (hi << 8));
  }

  // ── ALU primitives (shared, flag-correct) ─────────────────────────────────────

  private byte Adc(byte a, byte b) {
    var carry = this.GetFlag(FlagC) ? 1 : 0;
    var sum = a + b + carry;
    this.SetFlag(FlagC, sum > 0xFF);
    this.SetFlag(FlagH, ((a & 0x0F) + (b & 0x0F) + carry) > 0x0F);
    var result = (byte)sum;
    // Overflow: both operands same sign, result differs.
    this.SetFlag(FlagV, ((a ^ result) & (b ^ result) & 0x80) != 0);
    this.SetNz(result);
    return result;
  }

  private byte Sbc(byte a, byte b) {
    // Subtraction is addition of the one's-complement with the borrow as carry-in.
    var carry = this.GetFlag(FlagC) ? 1 : 0;
    var diff = a + (b ^ 0xFF) + carry;
    this.SetFlag(FlagC, diff > 0xFF);
    this.SetFlag(FlagH, ((a & 0x0F) + ((b ^ 0xFF) & 0x0F) + carry) > 0x0F);
    var result = (byte)diff;
    this.SetFlag(FlagV, ((a ^ result) & ((b ^ 0xFF) ^ result) & 0x80) != 0);
    this.SetNz(result);
    return result;
  }

  private void Cmp(byte a, byte b) {
    var diff = a - b;
    this.SetFlag(FlagC, diff >= 0);
    this.SetNz((byte)diff);
  }

  private byte Asl(byte v) {
    this.SetFlag(FlagC, (v & 0x80) != 0);
    var r = (byte)(v << 1);
    this.SetNz(r);
    return r;
  }

  private byte Lsr(byte v) {
    this.SetFlag(FlagC, (v & 0x01) != 0);
    var r = (byte)(v >> 1);
    this.SetNz(r);
    return r;
  }

  private byte Rol(byte v) {
    var carryIn = this.GetFlag(FlagC) ? 1 : 0;
    this.SetFlag(FlagC, (v & 0x80) != 0);
    var r = (byte)((v << 1) | carryIn);
    this.SetNz(r);
    return r;
  }

  private byte Ror(byte v) {
    var carryIn = this.GetFlag(FlagC) ? 0x80 : 0;
    this.SetFlag(FlagC, (v & 0x01) != 0);
    var r = (byte)((v >> 1) | carryIn);
    this.SetNz(r);
    return r;
  }

  private byte Inc(byte v) { var r = (byte)(v + 1); this.SetNz(r); return r; }
  private byte Dec(byte v) { var r = (byte)(v - 1); this.SetNz(r); return r; }
  private byte And(byte a, byte b) { var r = (byte)(a & b); this.SetNz(r); return r; }
  private byte Or(byte a, byte b) { var r = (byte)(a | b); this.SetNz(r); return r; }
  private byte Eor(byte a, byte b) { var r = (byte)(a ^ b); this.SetNz(r); return r; }

  // 16-bit word ALU (for ADDW/SUBW/CMPW/INCW/DECW/MOVW).
  private ushort Addw(ushort a, ushort b) {
    var sum = a + b;
    this.SetFlag(FlagC, sum > 0xFFFF);
    this.SetFlag(FlagH, ((a & 0x0FFF) + (b & 0x0FFF)) > 0x0FFF);
    var result = (ushort)sum;
    this.SetFlag(FlagV, ((a ^ result) & (b ^ result) & 0x8000) != 0);
    this.SetFlag(FlagZ, result == 0);
    this.SetFlag(FlagN, (result & 0x8000) != 0);
    return result;
  }

  private ushort Subw(ushort a, ushort b) {
    var diff = a - b;
    this.SetFlag(FlagC, diff >= 0);
    this.SetFlag(FlagH, ((a & 0x0FFF) - (b & 0x0FFF)) >= 0);
    var result = (ushort)diff;
    this.SetFlag(FlagV, ((a ^ b) & (a ^ result) & 0x8000) != 0);
    this.SetFlag(FlagZ, result == 0);
    this.SetFlag(FlagN, (result & 0x8000) != 0);
    return result;
  }

  // ── addressing-mode resolvers ──────────────────────────────────────────────────

  private byte ReadAbs(out ushort address) { address = this.ReadPc16(); return this.Read(address); }

  private ushort DpWordAddr(byte offset) => this.Dp(offset);

  private ushort ReadDpWord(byte offset) {
    var lo = this.ReadDp(offset);
    var hi = this.ReadDp((byte)(offset + 1));
    return (ushort)(lo | (hi << 8));
  }

  private void WriteDpWord(byte offset, ushort value) {
    this.WriteDp(offset, (byte)value);
    this.WriteDp((byte)(offset + 1), (byte)(value >> 8));
  }

  // [dp+X] indirect: pointer at dp+X (word) → effective address.
  private ushort IndexedIndirectX() {
    var dp = (byte)(this.ReadPc() + this.X);
    return this.ReadDpWord(dp);
  }

  // [dp]+Y indirect: pointer at dp (word) + Y → effective address.
  private ushort IndirectIndexedY() {
    var dp = this.ReadPc();
    return (ushort)(this.ReadDpWord(dp) + this.Y);
  }

  // ── branch helper ───────────────────────────────────────────────────────────────

  private int Branch(bool condition) {
    var rel = (sbyte)this.ReadPc();
    if (!condition)
      return 2;
    this.Pc = (ushort)(this.Pc + rel);
    return 4;
  }

  // ── absolute single-bit operand (mem.bit): 13-bit address + 3-bit position ───────

  private (ushort Address, int Bit) ReadMemBit() {
    var operand = this.ReadPc16();
    var address = (ushort)(operand & 0x1FFF);
    var bit = operand >> 13;
    return (address, bit);
  }

  // ── the dispatcher ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes and executes a single instruction at <see cref="Pc"/>, advancing the program
  /// counter and registers, and returns the master-cycle cost of that instruction.
  /// </summary>
  public int Step() {
    var opcode = this.ReadPc();
    switch (opcode) {

      // ── MOV into A ──
      case 0xE8: this.A = this.ReadPc(); this.SetNz(this.A); return 2;                  // MOV A,#imm
      case 0xE6: this.A = this.Read(this.Dp(this.X)); this.SetNz(this.A); return 3;       // MOV A,(X)
      case 0xBF: { var addr = this.Dp(this.X); this.A = this.Read(addr); this.X++; this.SetNz(this.A); return 4; } // MOV A,(X)+
      case 0xE4: this.A = this.ReadDp(this.ReadPc()); this.SetNz(this.A); return 3;      // MOV A,dp
      case 0xF4: this.A = this.ReadDp((byte)(this.ReadPc() + this.X)); this.SetNz(this.A); return 4; // MOV A,dp+X
      case 0xE5: this.A = this.ReadAbs(out _); this.SetNz(this.A); return 4;             // MOV A,!abs
      case 0xF5: { var a = (ushort)(this.ReadPc16() + this.X); this.A = this.Read(a); this.SetNz(this.A); return 5; } // MOV A,!abs+X
      case 0xF6: { var a = (ushort)(this.ReadPc16() + this.Y); this.A = this.Read(a); this.SetNz(this.A); return 5; } // MOV A,!abs+Y
      case 0xE7: this.A = this.Read(this.IndexedIndirectX()); this.SetNz(this.A); return 6; // MOV A,[dp+X]
      case 0xF7: this.A = this.Read(this.IndirectIndexedY()); this.SetNz(this.A); return 6; // MOV A,[dp]+Y

      // ── MOV into X / Y ──
      case 0xCD: this.X = this.ReadPc(); this.SetNz(this.X); return 2;                   // MOV X,#imm
      case 0xF8: this.X = this.ReadDp(this.ReadPc()); this.SetNz(this.X); return 3;       // MOV X,dp
      case 0xF9: this.X = this.ReadDp((byte)(this.ReadPc() + this.Y)); this.SetNz(this.X); return 4; // MOV X,dp+Y
      case 0xE9: this.X = this.ReadAbs(out _); this.SetNz(this.X); return 4;             // MOV X,!abs
      case 0x8D: this.Y = this.ReadPc(); this.SetNz(this.Y); return 2;                   // MOV Y,#imm
      case 0xEB: this.Y = this.ReadDp(this.ReadPc()); this.SetNz(this.Y); return 3;       // MOV Y,dp
      case 0xFB: this.Y = this.ReadDp((byte)(this.ReadPc() + this.X)); this.SetNz(this.Y); return 4; // MOV Y,dp+X
      case 0xEC: this.Y = this.ReadAbs(out _); this.SetNz(this.Y); return 4;             // MOV Y,!abs

      // ── MOV from A ──
      case 0xC6: this.Write(this.Dp(this.X), this.A); return 4;                           // MOV (X),A
      case 0xAF: this.Write(this.Dp(this.X), this.A); this.X++; return 4;                 // MOV (X)+,A
      case 0xC4: this.WriteDp(this.ReadPc(), this.A); return 4;                           // MOV dp,A
      case 0xD4: this.WriteDp((byte)(this.ReadPc() + this.X), this.A); return 5;          // MOV dp+X,A
      case 0xC5: { var a = this.ReadPc16(); this.Write(a, this.A); return 5; }            // MOV !abs,A
      case 0xD5: { var a = (ushort)(this.ReadPc16() + this.X); this.Write(a, this.A); return 6; } // MOV !abs+X,A
      case 0xD6: { var a = (ushort)(this.ReadPc16() + this.Y); this.Write(a, this.A); return 6; } // MOV !abs+Y,A
      case 0xC7: this.Write(this.IndexedIndirectX(), this.A); return 7;                    // MOV [dp+X],A
      case 0xD7: this.Write(this.IndirectIndexedY(), this.A); return 7;                    // MOV [dp]+Y,A

      // ── MOV from X / Y ──
      case 0xD8: this.WriteDp(this.ReadPc(), this.X); return 4;                            // MOV dp,X
      case 0xD9: this.WriteDp((byte)(this.ReadPc() + this.Y), this.X); return 5;           // MOV dp+Y,X
      case 0xC9: { var a = this.ReadPc16(); this.Write(a, this.X); return 5; }             // MOV !abs,X
      case 0xCB: this.WriteDp(this.ReadPc(), this.Y); return 4;                            // MOV dp,Y
      case 0xDB: this.WriteDp((byte)(this.ReadPc() + this.X), this.Y); return 5;           // MOV dp+X,Y
      case 0xCC: { var a = this.ReadPc16(); this.Write(a, this.Y); return 5; }             // MOV !abs,Y

      // ── MOV register ↔ register ──
      case 0x7D: this.A = this.X; this.SetNz(this.A); return 2;                            // MOV A,X
      case 0xDD: this.A = this.Y; this.SetNz(this.A); return 2;                            // MOV A,Y
      case 0x5D: this.X = this.A; this.SetNz(this.X); return 2;                            // MOV X,A
      case 0xFD: this.Y = this.A; this.SetNz(this.Y); return 2;                            // MOV Y,A
      case 0x9D: this.X = this.Sp; this.SetNz(this.X); return 2;                           // MOV X,SP
      case 0xBD: this.Sp = this.X; return 2;                                               // MOV SP,X (no flags)

      // ── MOV dp,dp / dp,#imm ──
      case 0xFA: { var src = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.ReadDp(src)); return 5; } // MOV dp,dp
      case 0x8F: { var imm = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, imm); return 5; }             // MOV dp,#imm

      // ── 16-bit MOVW / INCW / DECW / ADDW / SUBW / CMPW ──
      case 0xBA: { var dp = this.ReadPc(); var w = this.ReadDpWord(dp); this.Ya = w; this.SetFlag(FlagZ, w == 0); this.SetFlag(FlagN, (w & 0x8000) != 0); return 5; } // MOVW YA,dp
      case 0xDA: { var dp = this.ReadPc(); this.WriteDpWord(dp, this.Ya); return 5; }      // MOVW dp,YA (no flags)
      case 0x3A: { var dp = this.ReadPc(); var w = (ushort)(this.ReadDpWord(dp) + 1); this.WriteDpWord(dp, w); this.SetFlag(FlagZ, w == 0); this.SetFlag(FlagN, (w & 0x8000) != 0); return 6; } // INCW dp
      case 0x1A: { var dp = this.ReadPc(); var w = (ushort)(this.ReadDpWord(dp) - 1); this.WriteDpWord(dp, w); this.SetFlag(FlagZ, w == 0); this.SetFlag(FlagN, (w & 0x8000) != 0); return 6; } // DECW dp
      case 0x7A: { var dp = this.ReadPc(); this.Ya = this.Addw(this.Ya, this.ReadDpWord(dp)); return 5; } // ADDW YA,dp
      case 0x9A: { var dp = this.ReadPc(); this.Ya = this.Subw(this.Ya, this.ReadDpWord(dp)); return 5; } // SUBW YA,dp
      case 0x5A: { var dp = this.ReadPc(); this.Subw(this.Ya, this.ReadDpWord(dp)); return 4; } // CMPW YA,dp

      // ── MUL / DIV ──
      case 0xCF: { var product = this.Y * this.A; this.Ya = (ushort)product; this.SetFlag(FlagZ, this.Y == 0); this.SetFlag(FlagN, (this.Y & 0x80) != 0); return 9; } // MUL YA  (Y=high)
      case 0x9E: return this.Div();                                                        // DIV YA,X

      // ── arithmetic / logic with A (immediate, dp, abs, (X), [dp+X], [dp]+Y, dp+X, abs+X, abs+Y) ──
      case 0x88: this.A = this.Adc(this.A, this.ReadPc()); return 2;                       // ADC A,#imm
      case 0x84: this.A = this.Adc(this.A, this.ReadDp(this.ReadPc())); return 3;          // ADC A,dp
      case 0x94: this.A = this.Adc(this.A, this.ReadDp((byte)(this.ReadPc() + this.X))); return 4; // ADC A,dp+X
      case 0x85: this.A = this.Adc(this.A, this.ReadAbs(out _)); return 4;                 // ADC A,!abs
      case 0x95: this.A = this.Adc(this.A, this.Read((ushort)(this.ReadPc16() + this.X))); return 5; // ADC A,!abs+X
      case 0x96: this.A = this.Adc(this.A, this.Read((ushort)(this.ReadPc16() + this.Y))); return 5; // ADC A,!abs+Y
      case 0x86: this.A = this.Adc(this.A, this.Read(this.Dp(this.X))); return 3;          // ADC A,(X)
      case 0x87: this.A = this.Adc(this.A, this.Read(this.IndexedIndirectX())); return 6;  // ADC A,[dp+X]
      case 0x97: this.A = this.Adc(this.A, this.Read(this.IndirectIndexedY())); return 6;  // ADC A,[dp]+Y

      case 0xA8: this.A = this.Sbc(this.A, this.ReadPc()); return 2;                       // SBC A,#imm
      case 0xA4: this.A = this.Sbc(this.A, this.ReadDp(this.ReadPc())); return 3;          // SBC A,dp
      case 0xB4: this.A = this.Sbc(this.A, this.ReadDp((byte)(this.ReadPc() + this.X))); return 4; // SBC A,dp+X
      case 0xA5: this.A = this.Sbc(this.A, this.ReadAbs(out _)); return 4;                 // SBC A,!abs
      case 0xB5: this.A = this.Sbc(this.A, this.Read((ushort)(this.ReadPc16() + this.X))); return 5; // SBC A,!abs+X
      case 0xB6: this.A = this.Sbc(this.A, this.Read((ushort)(this.ReadPc16() + this.Y))); return 5; // SBC A,!abs+Y
      case 0xA6: this.A = this.Sbc(this.A, this.Read(this.Dp(this.X))); return 3;          // SBC A,(X)
      case 0xA7: this.A = this.Sbc(this.A, this.Read(this.IndexedIndirectX())); return 6;  // SBC A,[dp+X]
      case 0xB7: this.A = this.Sbc(this.A, this.Read(this.IndirectIndexedY())); return 6;  // SBC A,[dp]+Y

      case 0x68: this.Cmp(this.A, this.ReadPc()); return 2;                                // CMP A,#imm
      case 0x64: this.Cmp(this.A, this.ReadDp(this.ReadPc())); return 3;                   // CMP A,dp
      case 0x74: this.Cmp(this.A, this.ReadDp((byte)(this.ReadPc() + this.X))); return 4;  // CMP A,dp+X
      case 0x65: this.Cmp(this.A, this.ReadAbs(out _)); return 4;                          // CMP A,!abs
      case 0x75: this.Cmp(this.A, this.Read((ushort)(this.ReadPc16() + this.X))); return 5; // CMP A,!abs+X
      case 0x76: this.Cmp(this.A, this.Read((ushort)(this.ReadPc16() + this.Y))); return 5; // CMP A,!abs+Y
      case 0x66: this.Cmp(this.A, this.Read(this.Dp(this.X))); return 3;                   // CMP A,(X)
      case 0x67: this.Cmp(this.A, this.Read(this.IndexedIndirectX())); return 6;           // CMP A,[dp+X]
      case 0x77: this.Cmp(this.A, this.Read(this.IndirectIndexedY())); return 6;           // CMP A,[dp]+Y

      case 0x28: this.A = this.And(this.A, this.ReadPc()); return 2;                       // AND A,#imm
      case 0x24: this.A = this.And(this.A, this.ReadDp(this.ReadPc())); return 3;          // AND A,dp
      case 0x34: this.A = this.And(this.A, this.ReadDp((byte)(this.ReadPc() + this.X))); return 4; // AND A,dp+X
      case 0x25: this.A = this.And(this.A, this.ReadAbs(out _)); return 4;                 // AND A,!abs
      case 0x35: this.A = this.And(this.A, this.Read((ushort)(this.ReadPc16() + this.X))); return 5; // AND A,!abs+X
      case 0x36: this.A = this.And(this.A, this.Read((ushort)(this.ReadPc16() + this.Y))); return 5; // AND A,!abs+Y
      case 0x26: this.A = this.And(this.A, this.Read(this.Dp(this.X))); return 3;          // AND A,(X)
      case 0x27: this.A = this.And(this.A, this.Read(this.IndexedIndirectX())); return 6;  // AND A,[dp+X]
      case 0x37: this.A = this.And(this.A, this.Read(this.IndirectIndexedY())); return 6;  // AND A,[dp]+Y

      case 0x08: this.A = this.Or(this.A, this.ReadPc()); return 2;                        // OR A,#imm
      case 0x04: this.A = this.Or(this.A, this.ReadDp(this.ReadPc())); return 3;           // OR A,dp
      case 0x14: this.A = this.Or(this.A, this.ReadDp((byte)(this.ReadPc() + this.X))); return 4; // OR A,dp+X
      case 0x05: this.A = this.Or(this.A, this.ReadAbs(out _)); return 4;                  // OR A,!abs
      case 0x15: this.A = this.Or(this.A, this.Read((ushort)(this.ReadPc16() + this.X))); return 5; // OR A,!abs+X
      case 0x16: this.A = this.Or(this.A, this.Read((ushort)(this.ReadPc16() + this.Y))); return 5; // OR A,!abs+Y
      case 0x06: this.A = this.Or(this.A, this.Read(this.Dp(this.X))); return 3;           // OR A,(X)
      case 0x07: this.A = this.Or(this.A, this.Read(this.IndexedIndirectX())); return 6;   // OR A,[dp+X]
      case 0x17: this.A = this.Or(this.A, this.Read(this.IndirectIndexedY())); return 6;   // OR A,[dp]+Y

      case 0x48: this.A = this.Eor(this.A, this.ReadPc()); return 2;                       // EOR A,#imm
      case 0x44: this.A = this.Eor(this.A, this.ReadDp(this.ReadPc())); return 3;          // EOR A,dp
      case 0x54: this.A = this.Eor(this.A, this.ReadDp((byte)(this.ReadPc() + this.X))); return 4; // EOR A,dp+X
      case 0x45: this.A = this.Eor(this.A, this.ReadAbs(out _)); return 4;                 // EOR A,!abs
      case 0x55: this.A = this.Eor(this.A, this.Read((ushort)(this.ReadPc16() + this.X))); return 5; // EOR A,!abs+X
      case 0x56: this.A = this.Eor(this.A, this.Read((ushort)(this.ReadPc16() + this.Y))); return 5; // EOR A,!abs+Y
      case 0x46: this.A = this.Eor(this.A, this.Read(this.Dp(this.X))); return 3;          // EOR A,(X)
      case 0x47: this.A = this.Eor(this.A, this.Read(this.IndexedIndirectX())); return 6;  // EOR A,[dp+X]
      case 0x57: this.A = this.Eor(this.A, this.Read(this.IndirectIndexedY())); return 6;  // EOR A,[dp]+Y

      // ── (X),(Y) and memory-to-memory arithmetic ──
      case 0x99: { var v = this.Adc(this.Read(this.Dp(this.X)), this.Read(this.Dp(this.Y))); this.Write(this.Dp(this.X), v); return 5; } // ADC (X),(Y)
      case 0xB9: { var v = this.Sbc(this.Read(this.Dp(this.X)), this.Read(this.Dp(this.Y))); this.Write(this.Dp(this.X), v); return 5; } // SBC (X),(Y)
      case 0x79: this.Cmp(this.Read(this.Dp(this.X)), this.Read(this.Dp(this.Y))); return 5; // CMP (X),(Y)
      case 0x39: { var v = this.And(this.Read(this.Dp(this.X)), this.Read(this.Dp(this.Y))); this.Write(this.Dp(this.X), v); return 5; } // AND (X),(Y)
      case 0x19: { var v = this.Or(this.Read(this.Dp(this.X)), this.Read(this.Dp(this.Y))); this.Write(this.Dp(this.X), v); return 5; } // OR (X),(Y)
      case 0x59: { var v = this.Eor(this.Read(this.Dp(this.X)), this.Read(this.Dp(this.Y))); this.Write(this.Dp(this.X), v); return 5; } // EOR (X),(Y)

      // ── dp,dp and dp,#imm arithmetic ──
      case 0x89: { var src = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.Adc(this.ReadDp(dst), this.ReadDp(src))); return 6; } // ADC dp,dp
      case 0xA9: { var src = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.Sbc(this.ReadDp(dst), this.ReadDp(src))); return 6; } // SBC dp,dp
      case 0x69: { var src = this.ReadPc(); var dst = this.ReadPc(); this.Cmp(this.ReadDp(dst), this.ReadDp(src)); return 6; } // CMP dp,dp
      case 0x29: { var src = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.And(this.ReadDp(dst), this.ReadDp(src))); return 6; } // AND dp,dp
      case 0x09: { var src = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.Or(this.ReadDp(dst), this.ReadDp(src))); return 6; }  // OR dp,dp
      case 0x49: { var src = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.Eor(this.ReadDp(dst), this.ReadDp(src))); return 6; } // EOR dp,dp
      case 0x98: { var imm = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.Adc(this.ReadDp(dst), imm)); return 5; } // ADC dp,#imm
      case 0xB8: { var imm = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.Sbc(this.ReadDp(dst), imm)); return 5; } // SBC dp,#imm
      case 0x78: { var imm = this.ReadPc(); var dst = this.ReadPc(); this.Cmp(this.ReadDp(dst), imm); return 5; } // CMP dp,#imm
      case 0x38: { var imm = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.And(this.ReadDp(dst), imm)); return 5; } // AND dp,#imm
      case 0x18: { var imm = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.Or(this.ReadDp(dst), imm)); return 5; }  // OR dp,#imm
      case 0x58: { var imm = this.ReadPc(); var dst = this.ReadPc(); this.WriteDp(dst, this.Eor(this.ReadDp(dst), imm)); return 5; } // EOR dp,#imm

      // ── CMP X / CMP Y ──
      case 0xC8: this.Cmp(this.X, this.ReadPc()); return 2;                                // CMP X,#imm
      case 0x3E: this.Cmp(this.X, this.ReadDp(this.ReadPc())); return 3;                   // CMP X,dp
      case 0x1E: this.Cmp(this.X, this.ReadAbs(out _)); return 4;                          // CMP X,!abs
      case 0xAD: this.Cmp(this.Y, this.ReadPc()); return 2;                                // CMP Y,#imm
      case 0x7E: this.Cmp(this.Y, this.ReadDp(this.ReadPc())); return 3;                   // CMP Y,dp
      case 0x5E: this.Cmp(this.Y, this.ReadAbs(out _)); return 4;                          // CMP Y,!abs

      // ── INC / DEC ──
      case 0xBC: this.A = this.Inc(this.A); return 2;                                      // INC A
      case 0x3D: this.X = this.Inc(this.X); return 2;                                      // INC X
      case 0xFC: this.Y = this.Inc(this.Y); return 2;                                      // INC Y
      case 0xAB: { var dp = this.ReadPc(); this.WriteDp(dp, this.Inc(this.ReadDp(dp))); return 4; } // INC dp
      case 0xBB: { var dp = (byte)(this.ReadPc() + this.X); this.WriteDp(dp, this.Inc(this.ReadDp(dp))); return 5; } // INC dp+X
      case 0xAC: { var a = this.ReadPc16(); this.Write(a, this.Inc(this.Read(a))); return 5; } // INC !abs
      case 0x9C: this.A = this.Dec(this.A); return 2;                                      // DEC A
      case 0x1D: this.X = this.Dec(this.X); return 2;                                      // DEC X
      case 0xDC: this.Y = this.Dec(this.Y); return 2;                                      // DEC Y
      case 0x8B: { var dp = this.ReadPc(); this.WriteDp(dp, this.Dec(this.ReadDp(dp))); return 4; } // DEC dp
      case 0x9B: { var dp = (byte)(this.ReadPc() + this.X); this.WriteDp(dp, this.Dec(this.ReadDp(dp))); return 5; } // DEC dp+X
      case 0x8C: { var a = this.ReadPc16(); this.Write(a, this.Dec(this.Read(a))); return 5; } // DEC !abs

      // ── shift / rotate ──
      case 0x1C: this.A = this.Asl(this.A); return 2;                                      // ASL A
      case 0x0B: { var dp = this.ReadPc(); this.WriteDp(dp, this.Asl(this.ReadDp(dp))); return 4; } // ASL dp
      case 0x1B: { var dp = (byte)(this.ReadPc() + this.X); this.WriteDp(dp, this.Asl(this.ReadDp(dp))); return 5; } // ASL dp+X
      case 0x0C: { var a = this.ReadPc16(); this.Write(a, this.Asl(this.Read(a))); return 5; } // ASL !abs
      case 0x5C: this.A = this.Lsr(this.A); return 2;                                      // LSR A
      case 0x4B: { var dp = this.ReadPc(); this.WriteDp(dp, this.Lsr(this.ReadDp(dp))); return 4; } // LSR dp
      case 0x5B: { var dp = (byte)(this.ReadPc() + this.X); this.WriteDp(dp, this.Lsr(this.ReadDp(dp))); return 5; } // LSR dp+X
      case 0x4C: { var a = this.ReadPc16(); this.Write(a, this.Lsr(this.Read(a))); return 5; } // LSR !abs
      case 0x3C: this.A = this.Rol(this.A); return 2;                                      // ROL A
      case 0x2B: { var dp = this.ReadPc(); this.WriteDp(dp, this.Rol(this.ReadDp(dp))); return 4; } // ROL dp
      case 0x3B: { var dp = (byte)(this.ReadPc() + this.X); this.WriteDp(dp, this.Rol(this.ReadDp(dp))); return 5; } // ROL dp+X
      case 0x2C: { var a = this.ReadPc16(); this.Write(a, this.Rol(this.Read(a))); return 5; } // ROL !abs
      case 0x7C: this.A = this.Ror(this.A); return 2;                                      // ROR A
      case 0x6B: { var dp = this.ReadPc(); this.WriteDp(dp, this.Ror(this.ReadDp(dp))); return 4; } // ROR dp
      case 0x7B: { var dp = (byte)(this.ReadPc() + this.X); this.WriteDp(dp, this.Ror(this.ReadDp(dp))); return 5; } // ROR dp+X
      case 0x6C: { var a = this.ReadPc16(); this.Write(a, this.Ror(this.Read(a))); return 5; } // ROR !abs
      case 0x9F: this.A = (byte)((this.A >> 4) | (this.A << 4)); this.SetNz(this.A); return 5; // XCN A

      // ── DAA / DAS ──
      case 0xDF: this.Daa(); return 3;                                                     // DAA
      case 0xBE: this.Das(); return 3;                                                     // DAS

      // ── single-bit dp.bit operations (SET1/CLR1, BBS/BBC, TSET1/TCLR1) ──
      case 0x02: case 0x22: case 0x42: case 0x62:
      case 0x82: case 0xA2: case 0xC2: case 0xE2: { // SET1 dp.bit
        var bit = opcode >> 5; var dp = this.ReadPc();
        this.WriteDp(dp, (byte)(this.ReadDp(dp) | (1 << bit))); return 4;
      }
      case 0x12: case 0x32: case 0x52: case 0x72:
      case 0x92: case 0xB2: case 0xD2: case 0xF2: { // CLR1 dp.bit
        var bit = opcode >> 5; var dp = this.ReadPc();
        this.WriteDp(dp, (byte)(this.ReadDp(dp) & ~(1 << bit))); return 4;
      }
      case 0x03: case 0x23: case 0x43: case 0x63:
      case 0x83: case 0xA3: case 0xC3: case 0xE3: { // BBS dp.bit,rel
        var bit = opcode >> 5; var v = this.ReadDp(this.ReadPc());
        return this.Branch((v & (1 << bit)) != 0) + 1;
      }
      case 0x13: case 0x33: case 0x53: case 0x73:
      case 0x93: case 0xB3: case 0xD3: case 0xF3: { // BBC dp.bit,rel
        var bit = opcode >> 5; var v = this.ReadDp(this.ReadPc());
        return this.Branch((v & (1 << bit)) == 0) + 1;
      }
      case 0x0E: { var a = this.ReadPc16(); var v = this.Read(a); this.SetFlag(FlagZ, (this.A - v) == 0); this.SetFlag(FlagN, ((this.A - v) & 0x80) != 0); this.Write(a, (byte)(v | this.A)); return 6; } // TSET1 !abs
      case 0x4E: { var a = this.ReadPc16(); var v = this.Read(a); this.SetFlag(FlagZ, (this.A - v) == 0); this.SetFlag(FlagN, ((this.A - v) & 0x80) != 0); this.Write(a, (byte)(v & ~this.A)); return 6; } // TCLR1 !abs

      // ── absolute mem.bit carry operations ──
      case 0xAA: { var (addr, b) = this.ReadMemBit(); this.SetFlag(FlagC, (this.Read(addr) & (1 << b)) != 0); return 4; } // MOV1 C,mem.bit
      case 0xCA: { var (addr, b) = this.ReadMemBit(); var v = this.Read(addr); v = (byte)(this.GetFlag(FlagC) ? v | (1 << b) : v & ~(1 << b)); this.Write(addr, v); return 6; } // MOV1 mem.bit,C
      case 0x4A: { var (addr, b) = this.ReadMemBit(); this.SetFlag(FlagC, this.GetFlag(FlagC) && (this.Read(addr) & (1 << b)) != 0); return 4; } // AND1 C,mem.bit
      case 0x6A: { var (addr, b) = this.ReadMemBit(); this.SetFlag(FlagC, this.GetFlag(FlagC) && (this.Read(addr) & (1 << b)) == 0); return 4; } // AND1 C,/mem.bit
      case 0x0A: { var (addr, b) = this.ReadMemBit(); this.SetFlag(FlagC, this.GetFlag(FlagC) || (this.Read(addr) & (1 << b)) != 0); return 5; } // OR1 C,mem.bit
      case 0x2A: { var (addr, b) = this.ReadMemBit(); this.SetFlag(FlagC, this.GetFlag(FlagC) || (this.Read(addr) & (1 << b)) == 0); return 5; } // OR1 C,/mem.bit
      case 0x8A: { var (addr, b) = this.ReadMemBit(); this.SetFlag(FlagC, this.GetFlag(FlagC) ^ ((this.Read(addr) & (1 << b)) != 0)); return 5; } // EOR1 C,mem.bit
      case 0xEA: { var (addr, b) = this.ReadMemBit(); this.Write(addr, (byte)(this.Read(addr) ^ (1 << b))); return 5; } // NOT1 mem.bit

      // ── flag operations ──
      case 0x60: this.SetFlag(FlagC, false); return 2;                                     // CLRC
      case 0x80: this.SetFlag(FlagC, true); return 2;                                      // SETC
      case 0xED: this.SetFlag(FlagC, !this.GetFlag(FlagC)); return 3;                       // NOTC
      case 0xE0: this.SetFlag(FlagV, false); this.SetFlag(FlagH, false); return 2;          // CLRV
      case 0x20: this.SetFlag(FlagP, false); return 2;                                      // CLRP
      case 0x40: this.SetFlag(FlagP, true); return 2;                                       // SETP
      case 0xA0: this.SetFlag(FlagI, true); return 3;                                       // EI
      case 0xC0: this.SetFlag(FlagI, false); return 3;                                      // DI

      // ── stack ──
      case 0x2D: this.Push(this.A); return 4;                                              // PUSH A
      case 0x4D: this.Push(this.X); return 4;                                              // PUSH X
      case 0x6D: this.Push(this.Y); return 4;                                              // PUSH Y
      case 0x0D: this.Push(this.Psw); return 4;                                            // PUSH PSW
      case 0xAE: this.A = this.Pop(); return 4;                                            // POP A
      case 0xCE: this.X = this.Pop(); return 4;                                            // POP X
      case 0xEE: this.Y = this.Pop(); return 4;                                            // POP Y
      case 0x8E: this.Psw = this.Pop(); return 4;                                          // POP PSW

      // ── branches ──
      case 0x2F: return this.Branch(true);                                                 // BRA
      case 0xF0: return this.Branch(this.GetFlag(FlagZ));                                   // BEQ
      case 0xD0: return this.Branch(!this.GetFlag(FlagZ));                                  // BNE
      case 0xB0: return this.Branch(this.GetFlag(FlagC));                                   // BCS
      case 0x90: return this.Branch(!this.GetFlag(FlagC));                                  // BCC
      case 0x70: return this.Branch(this.GetFlag(FlagV));                                   // BVS
      case 0x50: return this.Branch(!this.GetFlag(FlagV));                                  // BVC
      case 0x30: return this.Branch(this.GetFlag(FlagN));                                   // BMI
      case 0x10: return this.Branch(!this.GetFlag(FlagN));                                  // BPL
      case 0x2E: { var dp = this.ReadPc(); var v = this.ReadDp(dp); return this.Branch(this.A != v) + 1; } // CBNE dp,rel
      case 0xDE: { var dp = (byte)(this.ReadPc() + this.X); var v = this.ReadDp(dp); return this.Branch(this.A != v) + 2; } // CBNE dp+X,rel
      case 0x6E: { var dp = this.ReadPc(); var v = (byte)(this.ReadDp(dp) - 1); this.WriteDp(dp, v); return this.Branch(v != 0) + 1; } // DBNZ dp,rel
      case 0xFE: { this.Y = (byte)(this.Y - 1); return this.Branch(this.Y != 0) + 2; }      // DBNZ Y,rel

      // ── jumps / calls ──
      case 0x5F: this.Pc = this.ReadPc16(); return 3;                                       // JMP !abs
      case 0x1F: { var a = (ushort)(this.ReadPc16() + this.X); var lo = this.Read(a); var hi = this.Read((ushort)(a + 1)); this.Pc = (ushort)(lo | (hi << 8)); return 6; } // JMP [!abs+X]
      case 0x3F: { var target = this.ReadPc16(); this.Push16(this.Pc); this.Pc = target; return 8; } // CALL !abs
      case 0x4F: { var n = this.ReadPc(); this.Push16(this.Pc); this.Pc = (ushort)(0xFF00 | n); return 6; } // PCALL up
      case 0x6F: this.Pc = this.Pop16(); return 5;                                          // RET
      case 0x7F: this.Psw = this.Pop(); this.Pc = this.Pop16(); return 6;                   // RETI

      // ── TCALL n (0x01,0x11,...,0xF1) ──
      case 0x01: case 0x11: case 0x21: case 0x31: case 0x41: case 0x51: case 0x61: case 0x71:
      case 0x81: case 0x91: case 0xA1: case 0xB1: case 0xC1: case 0xD1: case 0xE1: case 0xF1: {
        var n = opcode >> 4; // 0..15
        var vector = (ushort)(0xFFDE - n * 2);
        this.Push16(this.Pc);
        var lo = this.Read(vector);
        var hi = this.Read((ushort)(vector + 1));
        this.Pc = (ushort)(lo | (hi << 8));
        return 8;
      }

      // ── misc / control ──
      case 0x00: return 2;                                                                  // NOP
      case 0xEF: return 3;                                                                  // SLEEP (treated as a multi-cycle stall)
      case 0xFF: return 3;                                                                  // STOP (treated as a stall)
      case 0x0F: { this.Push16(this.Pc); this.Push(this.Psw); this.SetFlag(FlagB, true); this.SetFlag(FlagI, false); var lo = this.Read(0xFFDE); var hi = this.Read(0xFFDF); this.Pc = (ushort)(lo | (hi << 8)); return 8; } // BRK
    }

    // All 256 opcodes are enumerated above; this point is never reached.
  }

  // ── DIV with the documented half-carry quirk ──────────────────────────────────

  private int Div() {
    // The SPC700 DIV is a 9-bit restoring divide of YA by X. The documented behaviour
    // (and the result/flag model below) follows bsnes/higan's reference implementation.
    var ya = this.Ya;
    this.SetFlag(FlagH, (this.X & 0x0F) <= (this.Y & 0x0F));

    if (this.X == 0) {
      // Division by zero: result is undefined on hardware; set overflow and keep registers sane.
      this.SetFlag(FlagV, true);
      this.A = 0xFF;
      this.Y = (byte)ya;
      this.SetNz(this.A);
      return 12;
    }

    if ((this.Y & 0xFF) < (this.X << 1)) {
      this.A = (byte)(ya / this.X);
      this.Y = (byte)(ya % this.X);
    } else {
      this.A = (byte)(255 - (ya - (this.X << 9)) / (256 - this.X));
      this.Y = (byte)(this.X + (ya - (this.X << 9)) % (256 - this.X));
    }

    this.SetFlag(FlagV, this.A == 0 && (ya >> 8) >= this.X);
    this.SetNz(this.A);
    return 12;
  }

  // ── decimal adjust ────────────────────────────────────────────────────────────

  private void Daa() {
    if (this.GetFlag(FlagC) || this.A > 0x99) {
      this.A += 0x60;
      this.SetFlag(FlagC, true);
    }
    if (this.GetFlag(FlagH) || (this.A & 0x0F) > 0x09)
      this.A += 0x06;
    this.SetNz(this.A);
  }

  private void Das() {
    if (!this.GetFlag(FlagC) || this.A > 0x99) {
      this.A -= 0x60;
      this.SetFlag(FlagC, false);
    }
    if (!this.GetFlag(FlagH) || (this.A & 0x0F) > 0x09)
      this.A -= 0x06;
    this.SetNz(this.A);
  }
}
