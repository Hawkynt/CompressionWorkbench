#pragma warning disable CS1591
using Codec.Mos6502;

namespace Compression.Tests.Codecs.Mos6502;

[TestFixture]
public class Cpu6502Tests {

  /// <summary>Flat 64 KB RAM bus for hand-assembled test programs.</summary>
  private sealed class RamBus : IBus6502 {
    public readonly byte[] Ram = new byte[0x10000];
    public byte Read(ushort addr) => this.Ram[addr];
    public void Write(ushort addr, byte value) => this.Ram[addr] = value;
  }

  // Loads a program at $0200, points the reset vector there, and returns a fresh CPU.
  private static (Cpu6502 Cpu, RamBus Bus) Load(params byte[] program) {
    var bus = new RamBus();
    const ushort origin = 0x0200;
    program.CopyTo(bus.Ram, origin);
    bus.Ram[0xFFFC] = origin & 0xFF;
    bus.Ram[0xFFFD] = origin >> 8;
    return (new Cpu6502(bus), bus);
  }

  // Runs n instructions, returning total cycles.
  private static long Run(Cpu6502 cpu, int instructions) {
    long cycles = 0;
    for (var i = 0; i < instructions; ++i)
      cycles += cpu.Step();
    return cycles;
  }

  [Test]
  public void Lda_Immediate_SetsAccumulatorAndFlags() {
    var (cpu, _) = Load(0xA9, 0x42); // LDA #$42
    var c = Run(cpu, 1);
    Assert.That(cpu.A, Is.EqualTo(0x42));
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Zero), Is.False);
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Negative), Is.False);
    Assert.That(c, Is.EqualTo(2));
  }

  [Test]
  public void Lda_Immediate_Zero_SetsZeroFlag() {
    var (cpu, _) = Load(0xA9, 0x00); // LDA #$00
    Run(cpu, 1);
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Zero), Is.True);
  }

  [Test]
  public void Lda_Immediate_Negative_SetsNegativeFlag() {
    var (cpu, _) = Load(0xA9, 0x80); // LDA #$80
    Run(cpu, 1);
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Negative), Is.True);
  }

  [Test]
  public void Sta_ZeroPage_WritesMemory() {
    var (cpu, bus) = Load(0xA9, 0x37, 0x85, 0x10); // LDA #$37 ; STA $10
    Run(cpu, 2);
    Assert.That(bus.Ram[0x10], Is.EqualTo(0x37));
  }

  [Test]
  public void Lda_AbsoluteX_PageCross_AddsCycle() {
    var (cpu, bus) = Load(0xA2, 0x01, 0xBD, 0xFF, 0x02); // LDX #$01 ; LDA $02FF,X
    bus.Ram[0x0300] = 0xAB;
    Run(cpu, 1);          // LDX
    var c = cpu.Step();   // LDA $02FF,X → $0300 crosses page
    Assert.That(cpu.A, Is.EqualTo(0xAB));
    Assert.That(c, Is.EqualTo(5)); // 4 + 1 page cross
  }

  [Test]
  public void Lda_IndirectIndexed_ReadsThroughPointer() {
    // ($10),Y with pointer $0400 and Y=4 → reads $0404.
    var (cpu, bus) = Load(0xA0, 0x04, 0xB1, 0x10); // LDY #$04 ; LDA ($10),Y
    bus.Ram[0x10] = 0x00;
    bus.Ram[0x11] = 0x04;
    bus.Ram[0x0404] = 0x5A;
    Run(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0x5A));
  }

  [Test]
  public void Adc_Binary_SetsCarryAndOverflow() {
    // CLC ; LDA #$50 ; ADC #$50 → $A0, V set (positive+positive→negative), C clear.
    var (cpu, _) = Load(0x18, 0xA9, 0x50, 0x69, 0x50);
    Run(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0xA0));
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Overflow), Is.True);
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Carry), Is.False);
  }

  [Test]
  public void Adc_Decimal_BcdAddition() {
    // SED ; CLC ; LDA #$19 ; ADC #$01 → BCD 20.
    var (cpu, _) = Load(0xF8, 0x18, 0xA9, 0x19, 0x69, 0x01);
    Run(cpu, 4);
    Assert.That(cpu.A, Is.EqualTo(0x20));
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Carry), Is.False);
  }

  [Test]
  public void Adc_Decimal_CarryOut() {
    // SED ; CLC ; LDA #$99 ; ADC #$01 → BCD 00 with carry.
    var (cpu, _) = Load(0xF8, 0x18, 0xA9, 0x99, 0x69, 0x01);
    Run(cpu, 4);
    Assert.That(cpu.A, Is.EqualTo(0x00));
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Carry), Is.True);
  }

  [Test]
  public void Sbc_Decimal_BcdSubtraction() {
    // SED ; SEC ; LDA #$50 ; SBC #$25 → BCD 25.
    var (cpu, _) = Load(0xF8, 0x38, 0xA9, 0x50, 0xE9, 0x25);
    Run(cpu, 4);
    Assert.That(cpu.A, Is.EqualTo(0x25));
  }

  [Test]
  public void Sbc_Binary_Underflow_ClearsCarry() {
    // SEC ; LDA #$10 ; SBC #$20 → $F0, carry clear (borrow).
    var (cpu, _) = Load(0x38, 0xA9, 0x10, 0xE9, 0x20);
    Run(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0xF0));
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Carry), Is.False);
  }

  [Test]
  public void Branch_BneTaken_AddsCycle() {
    // LDA #$01 ; BNE +2 — taken, no page cross.
    var (cpu, _) = Load(0xA9, 0x01, 0xD0, 0x02);
    Run(cpu, 1);
    var c = cpu.Step();
    Assert.That(c, Is.EqualTo(3)); // 2 + 1 taken
  }

  [Test]
  public void Jsr_Rts_RoundTrip() {
    // $0200: JSR $0210 ; $0203: LDA #$11
    // $0210: LDA #$22 ; RTS
    var (cpu, bus) = Load(0x20, 0x10, 0x02, 0xA9, 0x11);
    bus.Ram[0x0210] = 0xA9; bus.Ram[0x0211] = 0x22; bus.Ram[0x0212] = 0x60;
    cpu.Step(); // JSR
    Assert.That(cpu.PC, Is.EqualTo(0x0210));
    cpu.Step(); // LDA #$22
    Assert.That(cpu.A, Is.EqualTo(0x22));
    cpu.Step(); // RTS
    Assert.That(cpu.PC, Is.EqualTo(0x0203));
  }

  [Test]
  public void Stack_PhaPla_PreservesValue() {
    // LDA #$7F ; PHA ; LDA #$00 ; PLA → A back to $7F.
    var (cpu, _) = Load(0xA9, 0x7F, 0x48, 0xA9, 0x00, 0x68);
    Run(cpu, 4);
    Assert.That(cpu.A, Is.EqualTo(0x7F));
  }

  [Test]
  public void Php_Plp_RoundTripsFlags() {
    // SEC ; PHP ; CLC ; PLP → carry restored.
    var (cpu, _) = Load(0x38, 0x08, 0x18, 0x28);
    Run(cpu, 4);
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Carry), Is.True);
  }

  [Test]
  public void IllegalLax_LoadsBothAandX() {
    // LAX $10 (opcode $A7).
    var (cpu, bus) = Load(0xA7, 0x10);
    bus.Ram[0x10] = 0x99;
    Run(cpu, 1);
    Assert.That(cpu.A, Is.EqualTo(0x99));
    Assert.That(cpu.X, Is.EqualTo(0x99));
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Negative), Is.True);
  }

  [Test]
  public void IllegalSax_StoresAandX() {
    // LDA #$F0 ; LDX #$0F ; SAX $20 → stores $00.
    var (cpu, bus) = Load(0xA9, 0xF0, 0xA2, 0x0F, 0x87, 0x20);
    Run(cpu, 3);
    Assert.That(bus.Ram[0x20], Is.EqualTo(0x00));
  }

  [Test]
  public void IllegalDcp_DecrementsThenCompares() {
    // LDA #$05 ; DCP $30 (mem starts $06 → $05); A==mem → Zero set, Carry set.
    var (cpu, bus) = Load(0xA9, 0x05, 0xC7, 0x30);
    bus.Ram[0x30] = 0x06;
    Run(cpu, 2);
    Assert.That(bus.Ram[0x30], Is.EqualTo(0x05));
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Zero), Is.True);
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Carry), Is.True);
  }

  [Test]
  public void Inc_Memory_CyclesAndWraps() {
    // INC $40 with $FF → $00, Zero set, 5 cycles.
    var (cpu, bus) = Load(0xE6, 0x40);
    bus.Ram[0x40] = 0xFF;
    var c = cpu.Step();
    Assert.That(bus.Ram[0x40], Is.EqualTo(0x00));
    Assert.That(cpu.P.HasFlag(Cpu6502.Status.Zero), Is.True);
    Assert.That(c, Is.EqualTo(5));
  }

  [Test]
  public void RunUntilRts_StopsAtMatchingReturn() {
    // Subroutine at $0300: LDA #$AA ; RTS.
    var bus = new RamBus();
    bus.Ram[0x0300] = 0xA9; bus.Ram[0x0301] = 0xAA; bus.Ram[0x0302] = 0x60;
    bus.Ram[0xFFFC] = 0x00; bus.Ram[0xFFFD] = 0x10; // reset vector irrelevant here
    var cpu = new Cpu6502(bus);
    var cycles = cpu.RunUntilRts(0x0300, 1000);
    Assert.That(cpu.A, Is.EqualTo(0xAA));
    Assert.That(cycles, Is.GreaterThan(0));
  }

  [Test]
  public void JmpIndirect_PageBoundaryBug() {
    // JMP ($07FF): low byte from $07FF, high byte from $0700 (NMOS bug, not $0800).
    // Program lives at $0200, so the bug-source page ($0700) is clear of the code.
    var (cpu, bus) = Load(0x6C, 0xFF, 0x07);
    bus.Ram[0x07FF] = 0x34;
    bus.Ram[0x0700] = 0x12; // bug source (low byte of next page used for high byte)
    bus.Ram[0x0800] = 0xFF; // would-be correct source
    cpu.Step();
    Assert.That(cpu.PC, Is.EqualTo(0x1234));
  }
}
