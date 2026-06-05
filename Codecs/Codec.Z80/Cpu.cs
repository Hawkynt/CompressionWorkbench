#pragma warning disable CS1591
namespace Codec.Z80;

/// <summary>
/// A cycle-counting Zilog Z80 CPU core sufficient to run ZX Spectrum AY and MSX KSS music
/// players. The decode covers the full documented instruction set:
/// <list type="bullet">
///   <item>the main (un-prefixed) opcode page — 8/16-bit loads, ALU, rotates, jumps,
///     calls, returns, restarts, the I/O group and the exchange/block-stub group;</item>
///   <item>the <c>CB</c> page — rotate/shift (<c>RLC,RRC,RL,RR,SLA,SRA,SLL,SRL</c>) plus
///     <c>BIT/RES/SET</c>;</item>
///   <item>the <c>ED</c> page — block transfer/search/I-O (<c>LDIR,LDDR,CPIR,CPDR,INIR,
///     OTIR,…</c>), 16-bit <c>ADC/SBC HL</c>, <c>NEG</c>, <c>RRD/RLD</c>, the interrupt-mode
///     selects <c>IM 0/1/2</c>, and the <c>I</c>/<c>R</c> register transfers;</item>
///   <item>the <c>DD</c>/<c>FD</c> pages — the <c>IX</c>/<c>IY</c> index registers with
///     signed displacement, including the <c>DDCB</c>/<c>FDCB</c> bit/rotate sub-page.</item>
/// </list>
/// The alternate register set (<c>AF'BC'DE'HL'</c>) and the <c>EXX</c>/<c>EX AF,AF'</c>
/// swaps are modelled.
/// <para>Flag handling implements S, Z, H, P/V, N and C exactly per the documented Z80
/// semantics. The two undocumented flag bits (bit 5 = "Y", bit 3 = "X") are propagated on a
/// best-effort basis: for 8-bit ALU results they copy bits 5/3 of the result, and for the
/// 16-bit arithmetic and bit-test instructions they follow the high-byte / tested-operand
/// convention. They are NOT guaranteed bit-exact against silicon for every edge case; music
/// players never branch on them, so this is documented as best-effort rather than verified.</para>
/// <para>T-state cycle counts are the standard documented per-instruction totals (including
/// the +5 for taken conditional <c>CALL</c>/block-repeat iterations); contended-memory and
/// wait-state timing are not modelled. Memory and ports are reached exclusively through
/// <see cref="IBusZ80"/>.</para>
/// </summary>
public sealed partial class Cpu {

  private readonly IBusZ80 _bus;

  // ── main register file ──────────────────────────────────────────────────────
  public byte A, F, B, C, D, E, H, L;
  // alternate set
  public byte A2, F2, B2, C2, D2, E2, H2, L2;
  // index + special
  public ushort IX, IY, SP, PC;
  public byte I, R;
  // interrupt state
  public bool IFF1, IFF2;
  public int InterruptMode; // 0, 1 or 2
  public bool Halted;

  /// <summary>Status-register flag bits (the F register layout).</summary>
  [Flags]
  public enum Flags : byte {
    C = 0x01,  // carry
    N = 0x02,  // add/subtract
    PV = 0x04, // parity / overflow
    X = 0x08,  // undocumented (copy of result bit 3)
    H = 0x10,  // half-carry
    Y = 0x20,  // undocumented (copy of result bit 5)
    Z = 0x40,  // zero
    S = 0x80,  // sign
  }

  public Cpu(IBusZ80 bus) {
    this._bus = bus;
    this.Reset();
  }

  /// <summary>Power-on/reset: PC=0, SP=0xFFFF, interrupts disabled, IM 0, AF=0xFFFF.</summary>
  public void Reset() {
    this.A = this.F = this.B = this.C = this.D = this.E = this.H = this.L = 0;
    this.A2 = this.F2 = this.B2 = this.C2 = this.D2 = this.E2 = this.H2 = this.L2 = 0;
    this.IX = this.IY = 0;
    this.PC = 0;
    this.SP = 0xFFFF;
    this.I = this.R = 0;
    this.IFF1 = this.IFF2 = false;
    this.InterruptMode = 0;
    this.Halted = false;
    this.A = 0xFF; this.F = 0xFF; // documented power-on AF
  }

  // ── 16-bit register pair accessors ──────────────────────────────────────────
  public ushort AF { get => (ushort)((this.A << 8) | this.F); set { this.A = (byte)(value >> 8); this.F = (byte)value; } }
  public ushort BC { get => (ushort)((this.B << 8) | this.C); set { this.B = (byte)(value >> 8); this.C = (byte)value; } }
  public ushort DE { get => (ushort)((this.D << 8) | this.E); set { this.D = (byte)(value >> 8); this.E = (byte)value; } }
  public ushort HL { get => (ushort)((this.H << 8) | this.L); set { this.H = (byte)(value >> 8); this.L = (byte)value; } }

  // ── bus helpers ─────────────────────────────────────────────────────────────
  private byte ReadMem(ushort addr) => this._bus.ReadMem(addr);
  private void WriteMem(ushort addr, byte value) => this._bus.WriteMem(addr, value);
  private byte ReadIo(ushort port) => this._bus.ReadIo(port);
  private void WriteIo(ushort port, byte value) => this._bus.WriteIo(port, value);

  private byte Fetch() {
    var value = this.ReadMem(this.PC++);
    this.R = (byte)((this.R & 0x80) | ((this.R + 1) & 0x7F)); // R increments bit 0-6 each M1
    return value;
  }

  // Operand fetch that must NOT bump R (the M1 increment already happened on the opcode).
  private byte FetchOperand() => this.ReadMem(this.PC++);

  private ushort FetchWord() {
    var lo = this.FetchOperand();
    var hi = this.FetchOperand();
    return (ushort)(lo | (hi << 8));
  }

  private void Push(ushort value) {
    this.WriteMem((ushort)(--this.SP), (byte)(value >> 8));
    this.WriteMem((ushort)(--this.SP), (byte)value);
  }

  private ushort Pop() {
    var lo = this.ReadMem(this.SP++);
    var hi = this.ReadMem(this.SP++);
    return (ushort)(lo | (hi << 8));
  }

  // ── flag helpers ────────────────────────────────────────────────────────────
  private void SetFlag(Flags flag, bool on) {
    if (on) this.F |= (byte)flag; else this.F &= (byte)~(byte)flag;
  }

  private bool HasFlag(Flags flag) => (this.F & (byte)flag) != 0;

  private static bool Parity(byte value) {
    var v = value;
    v ^= (byte)(v >> 4);
    v ^= (byte)(v >> 2);
    v ^= (byte)(v >> 1);
    return (v & 1) == 0; // true = even parity
  }

  // Sets S, Z, Y, X from a result; the caller sets H/PV/N/C explicitly.
  private void SetSzyx(byte value) {
    this.SetFlag(Flags.S, (value & 0x80) != 0);
    this.SetFlag(Flags.Z, value == 0);
    this.SetFlag(Flags.Y, (value & 0x20) != 0);
    this.SetFlag(Flags.X, (value & 0x08) != 0);
  }
}
