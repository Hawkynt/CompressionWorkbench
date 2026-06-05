#pragma warning disable CS1591
namespace Codec.Z80;

/// <summary>
/// Higher-level driving helpers used by music players: a "call a routine until it returns"
/// run loop and a maskable-interrupt injector.
/// </summary>
public sealed partial class Cpu {

  /// <summary>
  /// Calls the routine at <paramref name="address"/> using the music-player convention: a
  /// sentinel return address is pushed, execution jumps to the routine, and stepping stops
  /// once the routine's <c>RET</c> pops the sentinel back (PC == sentinel and SP restored)
  /// or <paramref name="maxCycles"/> is exhausted. Returns the T-states consumed.
  /// </summary>
  public long RunUntilRet(ushort address, long maxCycles) {
    const ushort sentinel = 0xFFFF;
    var targetSp = this.SP;
    this.Push(sentinel);
    this.PC = address;
    this.Halted = false;

    long cycles = 0;
    while (cycles < maxCycles) {
      if (this.PC == sentinel && this.SP == targetSp)
        break;
      cycles += this.Step();
    }
    return cycles;
  }

  /// <summary>
  /// Injects a maskable interrupt if interrupts are enabled (<see cref="IFF1"/>). The
  /// <paramref name="busValue"/> is the byte the interrupting device places on the data bus
  /// (used by IM 0 as the opcode to execute — typically an <c>RST</c> — and by IM 2 as the
  /// low byte of the vector-table pointer). IM 1 ignores it and vectors to <c>$0038</c>.
  /// Returns the T-states the acknowledge sequence consumed, or 0 when masked.
  /// <para>IM 0 here supports only the common single-byte <c>RST</c> form (e.g. <c>0xFF</c>
  /// = <c>RST 38</c>); multi-byte IM 0 opcodes are not modelled.</para>
  /// </summary>
  public long RaiseIrq(byte busValue) {
    if (!this.IFF1)
      return 0;

    this.IFF1 = this.IFF2 = false;
    this.Halted = false;

    switch (this.InterruptMode) {
      case 2: {
        var pointer = (ushort)((this.I << 8) | busValue);
        var lo = this.ReadMem(pointer);
        var hi = this.ReadMem((ushort)(pointer + 1));
        this.Push(this.PC);
        this.PC = (ushort)(lo | (hi << 8));
        return 19;
      }
      case 1:
        this.Push(this.PC);
        this.PC = 0x0038;
        return 13;
      default: // IM 0: execute the bus opcode (single-byte RST form).
        return this.Execute(busValue) + 2;
    }
  }
}
