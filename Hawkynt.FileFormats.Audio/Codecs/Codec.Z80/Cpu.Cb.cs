#pragma warning disable CS1591
namespace Codec.Z80;

/// <summary>
/// The <c>CB</c> opcode page: rotate/shift on a register or <c>(HL)</c>, and
/// <c>BIT/RES/SET</c>.
/// </summary>
public sealed partial class Cpu {

  private long ExecuteCb() {
    var opcode = this.Fetch();
    var op = (opcode >> 3) & 0x1F; // top 5 bits select the operation
    var reg = opcode & 0x07;
    var isMem = reg == 6;

    if (opcode < 0x40) {
      // rotate/shift group
      var value = this.GetReg(reg);
      var result = this.RotateShift(op, value);
      this.SetReg(reg, result);
      return isMem ? 15 : 8;
    }

    var bit = (opcode >> 3) & 0x07;
    if (opcode < 0x80) {
      // BIT b,r
      this.Bit(bit, this.GetReg(reg));
      return isMem ? 12 : 8;
    }

    if (opcode < 0xC0) {
      // RES b,r
      this.SetReg(reg, (byte)(this.GetReg(reg) & ~(1 << bit)));
      return isMem ? 15 : 8;
    }

    // SET b,r
    this.SetReg(reg, (byte)(this.GetReg(reg) | (1 << bit)));
    return isMem ? 15 : 8;
  }

  // op (0..7): RLC RRC RL RR SLA SRA SLL SRL
  private byte RotateShift(int op, byte value) => op switch {
    0 => this.Rlc(value),
    1 => this.Rrc(value),
    2 => this.Rl(value),
    3 => this.Rr(value),
    4 => this.Sla(value),
    5 => this.Sra(value),
    6 => this.Sll(value),
    _ => this.Srl(value),
  };
}
