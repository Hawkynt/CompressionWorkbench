#pragma warning disable CS1591
using Codec.Spc700;

namespace Compression.Tests.Spc;

/// <summary>
/// Hand-assembled SPC700 instruction tests. Each case loads a tiny program into ARAM at
/// <c>$0200</c>, seeds registers, steps a fixed number of instructions, and asserts the
/// resulting register/flag state. Programs are written as raw opcode bytes so the assertions
/// pin the exact decode path.
/// </summary>
[TestFixture]
public class Spc700CpuTests {

  private const byte FlagC = 0x01, FlagZ = 0x02, FlagH = 0x08, FlagP = 0x20, FlagV = 0x40, FlagN = 0x80;

  private static (Apu Apu, Spc700Cpu Cpu) Build(params byte[] program) {
    var apu = new Apu();
    program.CopyTo(apu.Ram.AsSpan(0x0200));
    var cpu = new Spc700Cpu(apu) { Pc = 0x0200, Sp = 0xFF };
    return (apu, cpu);
  }

  private static Spc700Cpu Run(int steps, params byte[] program) {
    var (_, cpu) = Build(program);
    for (var i = 0; i < steps; ++i)
      cpu.Step();
    return cpu;
  }

  // ── MOV / load-store ──

  [Test]
  public void MovImmediate_SetsAAndNzFlags() {
    var cpu = Run(1, 0xE8, 0x00); // MOV A,#0
    Assert.That(cpu.A, Is.EqualTo(0));
    Assert.That(cpu.Psw & FlagZ, Is.EqualTo(FlagZ));
  }

  [Test]
  public void MovImmediate_Negative_SetsNFlag() {
    var cpu = Run(1, 0xE8, 0x80); // MOV A,#$80
    Assert.That(cpu.Psw & FlagN, Is.EqualTo(FlagN));
    Assert.That(cpu.Psw & FlagZ, Is.EqualTo(0));
  }

  [Test]
  public void MovDpAndBack_RoundTripsThroughDirectPage() {
    var (apu, cpu) = Build(0xE8, 0x42, 0xC4, 0x10); // MOV A,#$42 ; MOV $10,A
    cpu.Step(); cpu.Step();
    Assert.That(apu.Ram[0x10], Is.EqualTo(0x42));
  }

  [Test]
  public void DirectPageFlag_SelectsPageOne() {
    // SETP ; MOV A,#$55 ; MOV $10,A  → should write $0110 not $0010.
    var (apu, cpu) = Build(0x40, 0xE8, 0x55, 0xC4, 0x10);
    cpu.Step(); cpu.Step(); cpu.Step();
    Assert.That(cpu.Psw & FlagP, Is.EqualTo(FlagP));
    Assert.That(apu.Ram[0x0110], Is.EqualTo(0x55));
    Assert.That(apu.Ram[0x0010], Is.EqualTo(0x00));
  }

  [Test]
  public void MovXPlusAutoIncrement_AdvancesX() {
    var (apu, cpu) = Build(0xAF); // MOV (X)+,A
    cpu.A = 0x99; cpu.X = 0x30;
    cpu.Step();
    Assert.That(apu.Ram[0x30], Is.EqualTo(0x99));
    Assert.That(cpu.X, Is.EqualTo(0x31));
  }

  // ── arithmetic + flags ──

  [Test]
  public void Adc_SetsCarryAndOverflow() {
    // CLRC ; MOV A,#$70 ; ADC A,#$70  → $E0, V set (pos+pos→neg), N set, C clear.
    var cpu = Run(3, 0x60, 0xE8, 0x70, 0x88, 0x70);
    Assert.That(cpu.A, Is.EqualTo(0xE0));
    Assert.That(cpu.Psw & FlagV, Is.EqualTo(FlagV));
    Assert.That(cpu.Psw & FlagN, Is.EqualTo(FlagN));
    Assert.That(cpu.Psw & FlagC, Is.EqualTo(0));
  }

  [Test]
  public void Adc_CarryOut() {
    // CLRC ; MOV A,#$FF ; ADC A,#$01 → $00, C set, Z set, H set.
    var cpu = Run(3, 0x60, 0xE8, 0xFF, 0x88, 0x01);
    Assert.That(cpu.A, Is.EqualTo(0x00));
    Assert.That(cpu.Psw & FlagC, Is.EqualTo(FlagC));
    Assert.That(cpu.Psw & FlagZ, Is.EqualTo(FlagZ));
    Assert.That(cpu.Psw & FlagH, Is.EqualTo(FlagH));
  }

  [Test]
  public void Sbc_BorrowProducesCarryClear() {
    // SETC ; MOV A,#$10 ; SBC A,#$20 → $F0 with borrow (C clear).
    var cpu = Run(3, 0x80, 0xE8, 0x10, 0xA8, 0x20);
    Assert.That(cpu.A, Is.EqualTo(0xF0));
    Assert.That(cpu.Psw & FlagC, Is.EqualTo(0));
  }

  [Test]
  public void Cmp_EqualSetsCarryAndZero() {
    // MOV A,#$42 ; CMP A,#$42 → C and Z set.
    var cpu = Run(2, 0xE8, 0x42, 0x68, 0x42);
    Assert.That(cpu.Psw & FlagC, Is.EqualTo(FlagC));
    Assert.That(cpu.Psw & FlagZ, Is.EqualTo(FlagZ));
  }

  // ── MUL / DIV ──

  [Test]
  public void Mul_MultipliesYaIntoYa() {
    // MOV A,#10 ; MOV Y,#20 ; MUL YA  → 200 in YA (Y=high, A=low).
    var cpu = Run(3, 0xE8, 0x0A, 0x8D, 0x14, 0xCF);
    Assert.That(cpu.A, Is.EqualTo(200 & 0xFF));
    Assert.That(cpu.Y, Is.EqualTo(200 >> 8));
  }

  [Test]
  public void Mul_LargeProductSetsHighByte() {
    // 100 * 100 = 10000 = 0x2710.
    var cpu = Run(3, 0xE8, 100, 0x8D, 100, 0xCF);
    Assert.That(cpu.A, Is.EqualTo(0x10));
    Assert.That(cpu.Y, Is.EqualTo(0x27));
  }

  [Test]
  public void Div_DividesYaByX() {
    // YA = 0x0064 (100), X = 7 → 100/7 = 14 rem 2.
    var cpu = Run(4, 0xE8, 0x64, 0x8D, 0x00, 0xCD, 0x07, 0x9E);
    Assert.That(cpu.A, Is.EqualTo(14));
    Assert.That(cpu.Y, Is.EqualTo(2));
  }

  [Test]
  public void Div_ByZero_SetsOverflow() {
    // X = 0 → overflow set, no exception.
    var cpu = Run(4, 0xE8, 0x10, 0x8D, 0x00, 0xCD, 0x00, 0x9E);
    Assert.That(cpu.Psw & FlagV, Is.EqualTo(FlagV));
  }

  // ── DAA / DAS ──

  [Test]
  public void Daa_AdjustsBinaryToBcd() {
    // MOV A,#$09 ; CLRC ; ADC A,#$01 → $0A ; DAA → $10.
    var cpu = Run(4, 0xE8, 0x09, 0x60, 0x88, 0x01, 0xDF);
    Assert.That(cpu.A, Is.EqualTo(0x10));
  }

  [Test]
  public void Das_AdjustsAfterSubtraction() {
    // MOV A,#$10 ; SETC ; SBC A,#$01 → $0F ; DAS → $09.
    var cpu = Run(4, 0xE8, 0x10, 0x80, 0xA8, 0x01, 0xBE);
    Assert.That(cpu.A, Is.EqualTo(0x09));
  }

  // ── logic / bit ops ──

  [Test]
  public void Xcn_SwapsNibbles() {
    var cpu = Run(2, 0xE8, 0x12, 0x9F); // MOV A,#$12 ; XCN A → $21
    Assert.That(cpu.A, Is.EqualTo(0x21));
  }

  [Test]
  public void Set1Clr1_ToggleDirectPageBit() {
    var (apu, cpu) = Build(0x02, 0x40, 0x12, 0x40); // SET1 $40.0 ; CLR1 $40.0
    cpu.Step();
    Assert.That(apu.Ram[0x40] & 0x01, Is.EqualTo(0x01));
    cpu.Step();
    Assert.That(apu.Ram[0x40] & 0x01, Is.EqualTo(0x00));
  }

  [Test]
  public void Or1_AbsoluteBit_SetsCarry() {
    // Put $01 at $0050 (bit 0 set). OR1 C,$0050.0 with C clear → C set.
    var (apu, cpu) = Build(0x60, 0x0A, 0x50, 0x00); // CLRC ; OR1 C,mem.bit (addr $050, bit0)
    apu.Ram[0x0050] = 0x01;
    cpu.Step(); cpu.Step();
    Assert.That(cpu.Psw & FlagC, Is.EqualTo(FlagC));
  }

  [Test]
  public void Mov1_CarryToMemoryBit() {
    // SETC ; MOV1 $0050.3,C → bit 3 of $0050 set.
    var operand = 0x0050 | (3 << 13);
    var (apu, cpu) = Build(0x80, 0xCA, (byte)operand, (byte)(operand >> 8));
    cpu.Step(); cpu.Step();
    Assert.That(apu.Ram[0x0050] & 0x08, Is.EqualTo(0x08));
  }

  // ── branches / control flow ──

  [Test]
  public void Bne_TakenWhenNotZero() {
    // MOV A,#1 ; CMP A,#2 ; BNE +2 ; (skipped) MOV A,#$EE  — branch skips the MOV.
    var cpu = Run(3, 0xE8, 0x01, 0x68, 0x02, 0xD0, 0x02, 0xE8, 0xEE);
    Assert.That(cpu.A, Is.Not.EqualTo(0xEE));
  }

  [Test]
  public void CallAndRet_RestorePc() {
    // CALL $0210 ; (at 0210) MOV A,#$AB ; RET back.
    var (apu, cpu) = Build(0x3F, 0x10, 0x02);
    apu.Ram[0x0210] = 0xE8; apu.Ram[0x0211] = 0xAB; apu.Ram[0x0212] = 0x6F; // MOV A,#$AB ; RET
    cpu.Step(); // CALL
    Assert.That(cpu.Pc, Is.EqualTo(0x0210));
    cpu.Step(); // MOV A,#$AB
    cpu.Step(); // RET
    Assert.That(cpu.A, Is.EqualTo(0xAB));
    Assert.That(cpu.Pc, Is.EqualTo(0x0203)); // return address after CALL operand
  }

  [Test]
  public void TCall_JumpsThroughVectorTable() {
    // TCALL 0 reads its target from $FFDE/$FFDF. Place a target there.
    var (apu, cpu) = Build(0x01); // TCALL 0
    apu.Ram[0xFFDE] = 0x34; apu.Ram[0xFFDF] = 0x12;
    cpu.Step();
    Assert.That(cpu.Pc, Is.EqualTo(0x1234));
  }

  [Test]
  public void PushPop_RoundTripsThroughStack() {
    // MOV A,#$5A ; PUSH A ; MOV A,#$00 ; POP A → A back to $5A.
    var cpu = Run(4, 0xE8, 0x5A, 0x2D, 0xE8, 0x00, 0xAE);
    Assert.That(cpu.A, Is.EqualTo(0x5A));
  }

  [Test]
  public void Dbnz_LoopsUntilZero() {
    // MOV Y,#3 ; (loop) DBNZ Y,-? ; should decrement Y to 0.
    // MOV Y,#3 at 0200; DBNZ Y,rel at 0202 with rel = -2 jumps back to itself.
    var cpu = Run(4, 0x8D, 0x03, 0xFE, 0xFE);
    Assert.That(cpu.Y, Is.EqualTo(0));
  }

  // ── word ops ──

  [Test]
  public void Movw_LoadsYaFromDirectPageWord() {
    // Put $34 at $20, $12 at $21. MOVW YA,$20 → A=$34, Y=$12.
    var (apu, cpu) = Build(0xBA, 0x20);
    apu.Ram[0x20] = 0x34; apu.Ram[0x21] = 0x12;
    cpu.Step();
    Assert.That(cpu.A, Is.EqualTo(0x34));
    Assert.That(cpu.Y, Is.EqualTo(0x12));
  }

  [Test]
  public void Incw_IncrementsDirectPageWord() {
    var (apu, cpu) = Build(0x3A, 0x20); // INCW $20
    apu.Ram[0x20] = 0xFF; apu.Ram[0x21] = 0x00; // 0x00FF → 0x0100
    cpu.Step();
    Assert.That(apu.Ram[0x20], Is.EqualTo(0x00));
    Assert.That(apu.Ram[0x21], Is.EqualTo(0x01));
  }
}
