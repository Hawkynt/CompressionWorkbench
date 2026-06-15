#pragma warning disable CS1591
namespace Codec.Z80;

/// <summary>
/// Arithmetic-logic primitives shared across the opcode pages. Each updates the F register
/// per the documented Z80 semantics (S, Z, H, P/V, N, C) and copies the undocumented Y/X
/// bits from the result.
/// </summary>
public sealed partial class Cpu {

  // ── 8-bit add/sub ───────────────────────────────────────────────────────────
  private void Add8(byte value, bool withCarry) {
    var carry = withCarry && this.HasFlag(Flags.C) ? 1 : 0;
    var sum = this.A + value + carry;
    var result = (byte)sum;
    var half = (this.A & 0x0F) + (value & 0x0F) + carry;
    this.SetFlag(Flags.C, sum > 0xFF);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.PV, ((this.A ^ value ^ 0x80) & (this.A ^ result) & 0x80) != 0);
    this.SetFlag(Flags.H, half > 0x0F);
    this.SetSzyx(result);
    this.A = result;
  }

  private void Sub8(byte value, bool withCarry, bool store) {
    var carry = withCarry && this.HasFlag(Flags.C) ? 1 : 0;
    var diff = this.A - value - carry;
    var result = (byte)diff;
    var half = (this.A & 0x0F) - (value & 0x0F) - carry;
    this.SetFlag(Flags.C, diff < 0);
    this.SetFlag(Flags.N, true);
    this.SetFlag(Flags.PV, ((this.A ^ value) & (this.A ^ result) & 0x80) != 0);
    this.SetFlag(Flags.H, (half & 0x10) != 0);
    // CP uses the operand's bits 5/3 for Y/X (documented quirk); SUB/SBC use the result's.
    if (store) {
      this.SetSzyx(result);
      this.A = result;
    } else {
      this.SetFlag(Flags.S, (result & 0x80) != 0);
      this.SetFlag(Flags.Z, result == 0);
      this.SetFlag(Flags.Y, (value & 0x20) != 0);
      this.SetFlag(Flags.X, (value & 0x08) != 0);
    }
  }

  private void And8(byte value) {
    this.A &= value;
    this.SetFlag(Flags.C, false);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, true);
    this.SetFlag(Flags.PV, Parity(this.A));
    this.SetSzyx(this.A);
  }

  private void Or8(byte value) {
    this.A |= value;
    this.SetFlag(Flags.C, false);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.PV, Parity(this.A));
    this.SetSzyx(this.A);
  }

  private void Xor8(byte value) {
    this.A ^= value;
    this.SetFlag(Flags.C, false);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.PV, Parity(this.A));
    this.SetSzyx(this.A);
  }

  private byte Inc8(byte value) {
    var result = (byte)(value + 1);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.PV, value == 0x7F);
    this.SetFlag(Flags.H, (value & 0x0F) == 0x0F);
    this.SetSzyx(result);
    return result;
  }

  private byte Dec8(byte value) {
    var result = (byte)(value - 1);
    this.SetFlag(Flags.N, true);
    this.SetFlag(Flags.PV, value == 0x80);
    this.SetFlag(Flags.H, (value & 0x0F) == 0x00);
    this.SetSzyx(result);
    return result;
  }

  // ── 16-bit arithmetic ───────────────────────────────────────────────────────
  private ushort Add16(ushort a, ushort b) {
    var sum = a + b;
    var result = (ushort)sum;
    var half = (a & 0x0FFF) + (b & 0x0FFF);
    this.SetFlag(Flags.C, sum > 0xFFFF);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, half > 0x0FFF);
    // Y/X from the high byte of the result; S/Z/PV unaffected by ADD HL.
    this.SetFlag(Flags.Y, (result & 0x2000) != 0);
    this.SetFlag(Flags.X, (result & 0x0800) != 0);
    return result;
  }

  private void Adc16(ushort value) {
    var carry = this.HasFlag(Flags.C) ? 1 : 0;
    var hl = this.HL;
    var sum = hl + value + carry;
    var result = (ushort)sum;
    var half = (hl & 0x0FFF) + (value & 0x0FFF) + carry;
    this.SetFlag(Flags.C, sum > 0xFFFF);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.PV, ((hl ^ value ^ 0x8000) & (hl ^ result) & 0x8000) != 0);
    this.SetFlag(Flags.H, half > 0x0FFF);
    this.SetFlag(Flags.S, (result & 0x8000) != 0);
    this.SetFlag(Flags.Z, result == 0);
    this.SetFlag(Flags.Y, (result & 0x2000) != 0);
    this.SetFlag(Flags.X, (result & 0x0800) != 0);
    this.HL = result;
  }

  private void Sbc16(ushort value) {
    var carry = this.HasFlag(Flags.C) ? 1 : 0;
    var hl = this.HL;
    var diff = hl - value - carry;
    var result = (ushort)diff;
    var half = (hl & 0x0FFF) - (value & 0x0FFF) - carry;
    this.SetFlag(Flags.C, diff < 0);
    this.SetFlag(Flags.N, true);
    this.SetFlag(Flags.PV, ((hl ^ value) & (hl ^ result) & 0x8000) != 0);
    this.SetFlag(Flags.H, (half & 0x1000) != 0);
    this.SetFlag(Flags.S, (result & 0x8000) != 0);
    this.SetFlag(Flags.Z, result == 0);
    this.SetFlag(Flags.Y, (result & 0x2000) != 0);
    this.SetFlag(Flags.X, (result & 0x0800) != 0);
    this.HL = result;
  }

  // ── rotates / shifts (accumulator quick forms RLCA/RRCA/RLA/RRA) ─────────────
  private void Rlca() {
    var carry = (this.A & 0x80) != 0;
    this.A = (byte)((this.A << 1) | (carry ? 1 : 0));
    this.SetFlag(Flags.C, carry);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.Y, (this.A & 0x20) != 0);
    this.SetFlag(Flags.X, (this.A & 0x08) != 0);
  }

  private void Rrca() {
    var carry = (this.A & 0x01) != 0;
    this.A = (byte)((this.A >> 1) | (carry ? 0x80 : 0));
    this.SetFlag(Flags.C, carry);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.Y, (this.A & 0x20) != 0);
    this.SetFlag(Flags.X, (this.A & 0x08) != 0);
  }

  private void Rla() {
    var carryIn = this.HasFlag(Flags.C) ? 1 : 0;
    var carryOut = (this.A & 0x80) != 0;
    this.A = (byte)((this.A << 1) | carryIn);
    this.SetFlag(Flags.C, carryOut);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.Y, (this.A & 0x20) != 0);
    this.SetFlag(Flags.X, (this.A & 0x08) != 0);
  }

  private void Rra() {
    var carryIn = this.HasFlag(Flags.C) ? 0x80 : 0;
    var carryOut = (this.A & 0x01) != 0;
    this.A = (byte)((this.A >> 1) | carryIn);
    this.SetFlag(Flags.C, carryOut);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.Y, (this.A & 0x20) != 0);
    this.SetFlag(Flags.X, (this.A & 0x08) != 0);
  }

  // ── CB-page rotate/shift primitives (full flag set) ─────────────────────────
  private byte Rlc(byte value) {
    var carry = (value & 0x80) != 0;
    var result = (byte)((value << 1) | (carry ? 1 : 0));
    this.RotateFlags(result, carry);
    return result;
  }

  private byte Rrc(byte value) {
    var carry = (value & 0x01) != 0;
    var result = (byte)((value >> 1) | (carry ? 0x80 : 0));
    this.RotateFlags(result, carry);
    return result;
  }

  private byte Rl(byte value) {
    var carryIn = this.HasFlag(Flags.C) ? 1 : 0;
    var carry = (value & 0x80) != 0;
    var result = (byte)((value << 1) | carryIn);
    this.RotateFlags(result, carry);
    return result;
  }

  private byte Rr(byte value) {
    var carryIn = this.HasFlag(Flags.C) ? 0x80 : 0;
    var carry = (value & 0x01) != 0;
    var result = (byte)((value >> 1) | carryIn);
    this.RotateFlags(result, carry);
    return result;
  }

  private byte Sla(byte value) {
    var carry = (value & 0x80) != 0;
    var result = (byte)(value << 1);
    this.RotateFlags(result, carry);
    return result;
  }

  private byte Sra(byte value) {
    var carry = (value & 0x01) != 0;
    var result = (byte)((value >> 1) | (value & 0x80));
    this.RotateFlags(result, carry);
    return result;
  }

  // SLL (undocumented): shift left, bit 0 set to 1.
  private byte Sll(byte value) {
    var carry = (value & 0x80) != 0;
    var result = (byte)((value << 1) | 1);
    this.RotateFlags(result, carry);
    return result;
  }

  private byte Srl(byte value) {
    var carry = (value & 0x01) != 0;
    var result = (byte)(value >> 1);
    this.RotateFlags(result, carry);
    return result;
  }

  private void RotateFlags(byte result, bool carry) {
    this.SetFlag(Flags.C, carry);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.PV, Parity(result));
    this.SetSzyx(result);
  }

  private void Bit(int bit, byte value) {
    var set = (value & (1 << bit)) != 0;
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, true);
    this.SetFlag(Flags.Z, !set);
    this.SetFlag(Flags.PV, !set);
    this.SetFlag(Flags.S, bit == 7 && set);
    // Y/X come from the tested byte (documented for the register/HL forms).
    this.SetFlag(Flags.Y, (value & 0x20) != 0);
    this.SetFlag(Flags.X, (value & 0x08) != 0);
  }

  // ── DAA, CPL, NEG, SCF, CCF ─────────────────────────────────────────────────
  private void Daa() {
    int a = this.A;
    var adjust = 0;
    var carry = this.HasFlag(Flags.C);
    if (this.HasFlag(Flags.H) || (a & 0x0F) > 9)
      adjust |= 0x06;
    if (carry || a > 0x99) {
      adjust |= 0x60;
      carry = true;
    }
    if (this.HasFlag(Flags.N)) {
      this.SetFlag(Flags.H, this.HasFlag(Flags.H) && (a & 0x0F) < 6);
      a -= adjust;
    } else {
      this.SetFlag(Flags.H, (a & 0x0F) > 9);
      a += adjust;
    }
    this.A = (byte)a;
    this.SetFlag(Flags.C, carry);
    this.SetFlag(Flags.PV, Parity(this.A));
    this.SetSzyx(this.A);
  }

  private void Cpl() {
    this.A = (byte)~this.A;
    this.SetFlag(Flags.N, true);
    this.SetFlag(Flags.H, true);
    this.SetFlag(Flags.Y, (this.A & 0x20) != 0);
    this.SetFlag(Flags.X, (this.A & 0x08) != 0);
  }

  private void Neg() {
    var value = this.A;
    this.A = 0;
    this.Sub8(value, withCarry: false, store: true);
  }

  private void Scf() {
    this.SetFlag(Flags.C, true);
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.H, false);
    this.SetFlag(Flags.Y, (this.A & 0x20) != 0);
    this.SetFlag(Flags.X, (this.A & 0x08) != 0);
  }

  private void Ccf() {
    this.SetFlag(Flags.H, this.HasFlag(Flags.C));
    this.SetFlag(Flags.C, !this.HasFlag(Flags.C));
    this.SetFlag(Flags.N, false);
    this.SetFlag(Flags.Y, (this.A & 0x20) != 0);
    this.SetFlag(Flags.X, (this.A & 0x08) != 0);
  }
}
