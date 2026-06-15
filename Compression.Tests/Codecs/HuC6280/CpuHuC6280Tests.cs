#pragma warning disable CS1591
using Codec.HuC6280;
using Codec.Mos6502;

namespace Compression.Tests.Codecs.HuC6280;

[TestFixture]
public class CpuHuC6280Tests {

  /// <summary>Flat 64 KB RAM bus for hand-assembled test programs (no MPR mapping).</summary>
  private sealed class RamBus : IBus6502 {
    public readonly byte[] Ram = new byte[0x10000];
    public byte Read(ushort addr) => this.Ram[addr];
    public void Write(ushort addr, byte value) => this.Ram[addr] = value;
  }

  // Loads a program at $0200, points the HuC6280 reset vector ($FFFE) there, returns a fresh CPU.
  private static (CpuHuC6280 Cpu, RamBus Bus) Load(params byte[] program) {
    var bus = new RamBus();
    const ushort origin = 0x0200;
    program.CopyTo(bus.Ram, origin);
    bus.Ram[0xFFFE] = origin & 0xFF;
    bus.Ram[0xFFFF] = origin >> 8;
    return (new CpuHuC6280(bus), bus);
  }

  private static long Run(CpuHuC6280 cpu, int instructions) {
    long cycles = 0;
    for (var i = 0; i < instructions; ++i)
      cycles += cpu.Step();
    return cycles;
  }

  // ── 65C02 base sanity (mirrors Mos6502 expectations) ──────────────────────────

  [Test]
  public void Lda_Immediate_SetsAccumulatorAndFlags() {
    var (cpu, _) = Load(0xA9, 0x42); // LDA #$42
    Run(cpu, 1);
    Assert.That(cpu.A, Is.EqualTo(0x42));
    Assert.That(cpu.P.HasFlag(CpuHuC6280.Status.Zero), Is.False);
    Assert.That(cpu.P.HasFlag(CpuHuC6280.Status.Negative), Is.False);
  }

  [Test]
  public void Sta_ZeroPage_WritesMemory() {
    var (cpu, bus) = Load(0xA9, 0x37, 0x85, 0x10); // LDA #$37 ; STA $10
    Run(cpu, 2);
    Assert.That(bus.Ram[0x10], Is.EqualTo(0x37));
  }

  [Test]
  public void Adc_Immediate_AddsWithCarry() {
    var (cpu, _) = Load(0x18, 0xA9, 0x20, 0x69, 0x22); // CLC ; LDA #$20 ; ADC #$22
    Run(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0x42));
    Assert.That(cpu.P.HasFlag(CpuHuC6280.Status.Carry), Is.False);
  }

  [Test]
  public void Jsr_Rts_RoundTrips() {
    // JSR $0210 ; (at $0210) LDA #$99 ; RTS
    var bus = new RamBus();
    new byte[] { 0x20, 0x10, 0x02 }.CopyTo(bus.Ram, 0x0200);
    bus.Ram[0x0210] = 0xA9; bus.Ram[0x0211] = 0x99; bus.Ram[0x0212] = 0x60;
    bus.Ram[0xFFFE] = 0x00; bus.Ram[0xFFFF] = 0x02;
    var cpu = new CpuHuC6280(bus);
    cpu.Step(); // JSR
    Assert.That(cpu.PC, Is.EqualTo(0x0210));
    cpu.Step(); // LDA
    cpu.Step(); // RTS
    Assert.That(cpu.A, Is.EqualTo(0x99));
    Assert.That(cpu.PC, Is.EqualTo(0x0203));
  }

  // ── HuC6280 register swaps ────────────────────────────────────────────────────

  [Test]
  public void Sxy_SwapsXandY() {
    var (cpu, _) = Load(0xA2, 0x11, 0xA0, 0x22, 0x02); // LDX #$11 ; LDY #$22 ; SXY
    Run(cpu, 3);
    Assert.That(cpu.X, Is.EqualTo(0x22));
    Assert.That(cpu.Y, Is.EqualTo(0x11));
  }

  [Test]
  public void Sax_SwapsAandX() {
    var (cpu, _) = Load(0xA9, 0xAB, 0xA2, 0xCD, 0x22); // LDA #$AB ; LDX #$CD ; SAX
    Run(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0xCD));
    Assert.That(cpu.X, Is.EqualTo(0xAB));
  }

  [Test]
  public void Cla_ClearsAccumulator() {
    var (cpu, _) = Load(0xA9, 0xFF, 0x62); // LDA #$FF ; CLA
    Run(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0));
  }

  // ── HuC6280 MPR bank mapper ───────────────────────────────────────────────────

  [Test]
  public void Tam_MapsAccumulatorIntoSelectedMprRegisters() {
    // LDA #$80 ; TAM #$05 (set MPR0 and MPR2 to $80)
    var (cpu, _) = Load(0xA9, 0x80, 0x53, 0x05);
    Run(cpu, 2);
    Assert.That(cpu.Mpr[0], Is.EqualTo(0x80));
    Assert.That(cpu.Mpr[2], Is.EqualTo(0x80));
    Assert.That(cpu.Mpr[1], Is.EqualTo(0x00));
  }

  [Test]
  public void Tma_ReadsSelectedMprRegisterIntoAccumulator() {
    var (cpu, _) = Load(0x43, 0x08); // TMA #$08 → reads MPR3
    cpu.Mpr[3] = 0x42;
    Run(cpu, 1);
    Assert.That(cpu.A, Is.EqualTo(0x42));
  }

  [Test]
  public void TamTma_RoundTrips() {
    // LDA #$77 ; TAM #$10 (MPR4) ; CLA ; TMA #$10
    var (cpu, _) = Load(0xA9, 0x77, 0x53, 0x10, 0x62, 0x43, 0x10);
    Run(cpu, 4);
    Assert.That(cpu.A, Is.EqualTo(0x77), "TMA should read back what TAM stored");
  }

  // ── HuC6280 block transfers ───────────────────────────────────────────────────

  [Test]
  public void Tii_CopiesBlockIncrementing() {
    // TII $0300,$0400,$0004 : copy 4 bytes from $0300 to $0400, both incrementing.
    var (cpu, bus) = Load(0x73, 0x00, 0x03, 0x00, 0x04, 0x04, 0x00);
    bus.Ram[0x0300] = 0x11; bus.Ram[0x0301] = 0x22; bus.Ram[0x0302] = 0x33; bus.Ram[0x0303] = 0x44;
    var c = cpu.Step();
    Assert.That(bus.Ram[0x0400], Is.EqualTo(0x11));
    Assert.That(bus.Ram[0x0401], Is.EqualTo(0x22));
    Assert.That(bus.Ram[0x0402], Is.EqualTo(0x33));
    Assert.That(bus.Ram[0x0403], Is.EqualTo(0x44));
    Assert.That(c, Is.EqualTo(17 + 6 * 4), "TII = 17 + 6/byte");
  }

  [Test]
  public void Tdd_CopiesBlockDecrementing() {
    // TDD $0303,$0403,$0004 : copy 4 bytes downward.
    var (cpu, bus) = Load(0xC3, 0x03, 0x03, 0x03, 0x04, 0x04, 0x00);
    bus.Ram[0x0303] = 0xAA; bus.Ram[0x0302] = 0xBB; bus.Ram[0x0301] = 0xCC; bus.Ram[0x0300] = 0xDD;
    cpu.Step();
    Assert.That(bus.Ram[0x0403], Is.EqualTo(0xAA));
    Assert.That(bus.Ram[0x0402], Is.EqualTo(0xBB));
    Assert.That(bus.Ram[0x0401], Is.EqualTo(0xCC));
    Assert.That(bus.Ram[0x0400], Is.EqualTo(0xDD));
  }

  [Test]
  public void Tin_CopiesToFixedDestination() {
    // TIN $0300,$0400,$0003 : source increments, destination fixed → last source byte lands.
    var (cpu, bus) = Load(0xD3, 0x00, 0x03, 0x00, 0x04, 0x03, 0x00);
    bus.Ram[0x0300] = 0x01; bus.Ram[0x0301] = 0x02; bus.Ram[0x0302] = 0x03;
    cpu.Step();
    Assert.That(bus.Ram[0x0400], Is.EqualTo(0x03), "fixed destination holds the last copied byte");
    Assert.That(bus.Ram[0x0401], Is.EqualTo(0x00), "destination does not advance");
  }

  [Test]
  public void Tia_AlternatesDestination() {
    // TIA $0300,$0400,$0004 : source increments, destination ping-pongs $0400/$0401.
    var (cpu, bus) = Load(0xE3, 0x00, 0x03, 0x00, 0x04, 0x04, 0x00);
    bus.Ram[0x0300] = 0x10; bus.Ram[0x0301] = 0x20; bus.Ram[0x0302] = 0x30; bus.Ram[0x0303] = 0x40;
    cpu.Step();
    // bytes 0,2 → $0400 (last wins = 0x30); bytes 1,3 → $0401 (last wins = 0x40)
    Assert.That(bus.Ram[0x0400], Is.EqualTo(0x30));
    Assert.That(bus.Ram[0x0401], Is.EqualTo(0x40));
  }

  [Test]
  public void Tai_AlternatesSource() {
    // TAI $0300,$0400,$0004 : source ping-pongs $0300/$0301, destination increments.
    var (cpu, bus) = Load(0xF3, 0x00, 0x03, 0x00, 0x04, 0x04, 0x00);
    bus.Ram[0x0300] = 0xA0; bus.Ram[0x0301] = 0xB0;
    cpu.Step();
    Assert.That(bus.Ram[0x0400], Is.EqualTo(0xA0));
    Assert.That(bus.Ram[0x0401], Is.EqualTo(0xB0));
    Assert.That(bus.Ram[0x0402], Is.EqualTo(0xA0));
    Assert.That(bus.Ram[0x0403], Is.EqualTo(0xB0));
  }

  // ── HuC6280 TST, speed switch, ST ports ───────────────────────────────────────

  [Test]
  public void Tst_SetsZeroWhenNoBitsOverlap() {
    // TST #$0F,$10  with mem[$10] = $F0 → no overlap → Z set, N from mem bit7.
    var (cpu, bus) = Load(0x83, 0x0F, 0x10);
    bus.Ram[0x10] = 0xF0;
    cpu.Step();
    Assert.That(cpu.P.HasFlag(CpuHuC6280.Status.Zero), Is.True);
    Assert.That(cpu.P.HasFlag(CpuHuC6280.Status.Negative), Is.True);
    Assert.That(bus.Ram[0x10], Is.EqualTo(0xF0), "TST must not modify the operand");
  }

  [Test]
  public void Csh_Csl_ToggleSpeedFlag() {
    var (cpu, _) = Load(0xD4, 0x54); // CSH ; CSL
    cpu.Step();
    Assert.That(cpu.HighSpeed, Is.True);
    cpu.Step();
    Assert.That(cpu.HighSpeed, Is.False);
  }

  [Test]
  public void Bra_AlwaysBranches() {
    var (cpu, _) = Load(0x80, 0x02, 0xA9, 0xEE, 0xA9, 0x55); // BRA +2 ; (skip LDA #$EE) ; LDA #$55
    cpu.Step(); // BRA
    cpu.Step(); // LDA #$55
    Assert.That(cpu.A, Is.EqualTo(0x55));
  }

  [Test]
  public void Bbr_BranchesWhenBitClear() {
    // BBR0 $10,+2 with mem[$10] bit0 clear → branch taken.
    var (cpu, bus) = Load(0x0F, 0x10, 0x02, 0xA9, 0xEE, 0xA9, 0x55);
    bus.Ram[0x10] = 0xFE; // bit0 = 0
    cpu.Step(); // BBR0 → branch
    cpu.Step(); // LDA #$55
    Assert.That(cpu.A, Is.EqualTo(0x55));
  }

  [Test]
  public void Smb_SetsBit() {
    var (cpu, bus) = Load(0x87, 0x10); // SMB0 $10
    bus.Ram[0x10] = 0x00;
    cpu.Step();
    Assert.That(bus.Ram[0x10], Is.EqualTo(0x01));
  }
}
