#pragma warning disable CS1591
using Codec.GameBoyApu;

namespace Compression.Tests.Codecs.GbApu;

[TestFixture]
public class Sm83CpuTests {

  // A flat 64 KB RAM bus so any address is readable/writable.
  private sealed class FlatBus : ISm83Bus {
    public readonly byte[] Memory = new byte[0x10000];
    public byte Read(ushort addr) => this.Memory[addr];
    public void Write(ushort addr, byte value) => this.Memory[addr] = value;
  }

  private static (Sm83Cpu Cpu, FlatBus Bus) Make(params byte[] program) {
    var bus = new FlatBus();
    program.CopyTo(bus.Memory, 0x0200);
    var cpu = new Sm83Cpu(bus) { PC = 0x0200, SP = 0xFFFE };
    return (cpu, bus);
  }

  // Runs a fixed number of instructions.
  private static long RunSteps(Sm83Cpu cpu, int count) {
    long cycles = 0;
    for (var i = 0; i < count; ++i) cycles += cpu.Step();
    return cycles;
  }

  [Test]
  public void LdImmediate_LoadsA() {
    var (cpu, _) = Make(0x3E, 0x42); // LD A,$42
    cpu.Step();
    Assert.That(cpu.A, Is.EqualTo(0x42));
  }

  [Test]
  public void Ld16Immediate_LoadsHl() {
    var (cpu, _) = Make(0x21, 0x34, 0x12); // LD HL,$1234
    cpu.Step();
    Assert.That(cpu.HL, Is.EqualTo(0x1234));
  }

  [Test]
  public void LdHlIncrement_StoresAndPostIncrements() {
    var (cpu, bus) = Make(
      0x21, 0x00, 0xC0,  // LD HL,$C000
      0x3E, 0x99,        // LD A,$99
      0x22);             // LD (HL+),A
    RunSteps(cpu, 3);
    Assert.That(bus.Memory[0xC000], Is.EqualTo(0x99));
    Assert.That(cpu.HL, Is.EqualTo(0xC001));
  }

  [Test]
  public void AddA_SetsHalfCarryAndCarry() {
    var (cpu, _) = Make(
      0x3E, 0x08,  // LD A,$08
      0x06, 0x09,  // LD B,$09
      0x80);       // ADD A,B  → $11, half-carry set
    RunSteps(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0x11));
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.HalfCarry, Is.Not.Zero);
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.Carry, Is.Zero);
  }

  [Test]
  public void SubA_ToZero_SetsZeroAndSubtract() {
    var (cpu, _) = Make(
      0x3E, 0x05,  // LD A,$05
      0xD6, 0x05); // SUB $05  → 0, Z set
    RunSteps(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0));
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.Zero, Is.Not.Zero);
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.Subtract, Is.Not.Zero);
  }

  [Test]
  public void Daa_AfterBcdAddition_Adjusts() {
    // 0x15 + 0x27 = 0x3C binary; DAA → 0x42 (15 + 27 = 42 decimal).
    var (cpu, _) = Make(
      0x3E, 0x15,  // LD A,$15
      0xC6, 0x27,  // ADD A,$27 → $3C
      0x27);       // DAA → $42
    RunSteps(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0x42));
  }

  [Test]
  public void Daa_AfterBcdSubtraction_Adjusts() {
    // 0x42 - 0x15 = 0x2D binary; DAA after subtract → 0x27 (42 - 15 = 27 decimal).
    var (cpu, _) = Make(
      0x3E, 0x42,  // LD A,$42
      0xD6, 0x15,  // SUB $15 → $2D
      0x27);       // DAA → $27
    RunSteps(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0x27));
  }

  [Test]
  public void IncWrapsAndSetsZeroHalfCarry() {
    var (cpu, _) = Make(
      0x3E, 0xFF,  // LD A,$FF
      0x3C);       // INC A → 0, Z + H set, carry untouched
    RunSteps(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0));
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.Zero, Is.Not.Zero);
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.HalfCarry, Is.Not.Zero);
  }

  [Test]
  public void JrSignedBackward_Loops() {
    // A counter loop: B starts 3, DEC B; JR NZ,-3 back to DEC.
    var (cpu, _) = Make(
      0x06, 0x03,  // LD B,3
      0x05,        // DEC B            (at $0202)
      0x20, 0xFD); // JR NZ,-3 → back to $0202
    cpu.Step(); // LD B,3
    // Execute DEC/JR pairs until B hits 0.
    for (var i = 0; i < 6; ++i) cpu.Step();
    Assert.That(cpu.B, Is.EqualTo(0));
  }

  [Test]
  public void CallAndRet_RoundTrip() {
    var (cpu, bus) = Make(
      0xCD, 0x10, 0x02, // CALL $0210
      0x76);            // HALT (landing pad after return)
    // Subroutine at $0210: LD A,$AB ; RET
    bus.Memory[0x0210] = 0x3E; bus.Memory[0x0211] = 0xAB; bus.Memory[0x0212] = 0xC9;
    cpu.Step();                 // CALL
    Assert.That(cpu.PC, Is.EqualTo(0x0210));
    cpu.Step();                 // LD A,$AB
    cpu.Step();                 // RET
    Assert.That(cpu.A, Is.EqualTo(0xAB));
    Assert.That(cpu.PC, Is.EqualTo(0x0203)); // instruction after CALL
  }

  [Test]
  public void PushPop_PreservesRegisterPair() {
    var (cpu, _) = Make(
      0x01, 0xCD, 0xAB, // LD BC,$ABCD
      0xC5,             // PUSH BC
      0xE1);            // POP HL
    RunSteps(cpu, 3);
    Assert.That(cpu.HL, Is.EqualTo(0xABCD));
  }

  [Test]
  public void Cb_BitTest_SetsZeroWhenBitClear() {
    var (cpu, _) = Make(
      0x3E, 0x00,  // LD A,0
      0xCB, 0x7F); // BIT 7,A → Z set
    RunSteps(cpu, 2);
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.Zero, Is.Not.Zero);
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.HalfCarry, Is.Not.Zero);
  }

  [Test]
  public void Cb_SetAndRes_ModifyBit() {
    var (cpu, _) = Make(
      0x3E, 0x00,  // LD A,0
      0xCB, 0xC7,  // SET 0,A → 1
      0xCB, 0xFF,  // SET 7,A → $81
      0xCB, 0x87); // RES 0,A → $80
    RunSteps(cpu, 4);
    Assert.That(cpu.A, Is.EqualTo(0x80));
  }

  [Test]
  public void Cb_Swap_ExchangesNibbles() {
    var (cpu, _) = Make(
      0x3E, 0x4B,  // LD A,$4B
      0xCB, 0x37); // SWAP A → $B4
    RunSteps(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0xB4));
  }

  [Test]
  public void Cb_Rl_RotatesThroughCarry() {
    var (cpu, _) = Make(
      0x37,        // SCF (carry = 1)
      0x3E, 0x80,  // LD A,$80
      0xCB, 0x17); // RL A → carry-in 1 → $01, carry-out 1
    RunSteps(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0x01));
    Assert.That(cpu.F & (byte)Sm83Cpu.Flags.Carry, Is.Not.Zero);
  }

  [Test]
  public void Ldh_WritesHighPage() {
    var (cpu, bus) = Make(
      0x3E, 0x77,  // LD A,$77
      0xE0, 0x80); // LDH ($80),A → writes $FF80
    RunSteps(cpu, 2);
    Assert.That(bus.Memory[0xFF80], Is.EqualTo(0x77));
  }

  [Test]
  public void AddSpSigned_NegativeOffset() {
    var (cpu, _) = Make(0xE8, 0xFE); // ADD SP,-2 ; SP starts $FFFE
    cpu.Step();
    Assert.That(cpu.SP, Is.EqualTo(0xFFFC));
  }

  [Test]
  public void RunUntilRet_StopsAtMatchingRet() {
    var bus = new FlatBus();
    // Routine at $0300: LD A,$5A ; RET.
    bus.Memory[0x0300] = 0x3E; bus.Memory[0x0301] = 0x5A; bus.Memory[0x0302] = 0xC9;
    var cpu = new Sm83Cpu(bus) { SP = 0xFFFE };
    var cycles = cpu.RunUntilRet(0x0300, 1000);
    Assert.That(cpu.A, Is.EqualTo(0x5A));
    Assert.That(cycles, Is.GreaterThan(0));
  }
}
