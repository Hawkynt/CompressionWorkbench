#pragma warning disable CS1591
using Codec.Z80;

namespace Compression.Tests.Codecs.Z80;

[TestFixture]
public class CpuTests {

  /// <summary>Flat 64 KB RAM + a recording I/O bus for hand-assembled test programs.</summary>
  private sealed class RamBus : IBusZ80 {
    public readonly byte[] Ram = new byte[0x10000];
    public readonly byte[] Ports = new byte[0x10000];
    public readonly List<(ushort Port, byte Value)> OutLog = [];

    public byte ReadMem(ushort addr) => this.Ram[addr];
    public void WriteMem(ushort addr, byte value) => this.Ram[addr] = value;
    public byte ReadIo(ushort port) => this.Ports[port];
    public void WriteIo(ushort port, byte value) { this.Ports[port] = value; this.OutLog.Add((port, value)); }
  }

  // Loads a program at $0100 and returns a fresh CPU with PC there.
  private static (Cpu Cpu, RamBus Bus) Load(params byte[] program) {
    var bus = new RamBus();
    const ushort origin = 0x0100;
    program.CopyTo(bus.Ram, origin);
    var cpu = new Cpu(bus) { PC = origin, SP = 0xFFF0 };
    return (cpu, bus);
  }

  private static long Run(Cpu cpu, int instructions) {
    long cycles = 0;
    for (var i = 0; i < instructions; ++i)
      cycles += cpu.Step();
    return cycles;
  }

  private static bool Flag(Cpu cpu, Cpu.Flags f) => (cpu.F & (byte)f) != 0;

  [Test]
  public void LdImmediate_LoadsRegister() {
    var (cpu, _) = Load(0x3E, 0x42); // LD A,$42
    var c = Run(cpu, 1);
    Assert.That(cpu.A, Is.EqualTo(0x42));
    Assert.That(c, Is.EqualTo(7));
  }

  [Test]
  public void Ld16_AndRegisterCopy() {
    // LD BC,$1234 ; LD A,B ; LD H,C
    var (cpu, _) = Load(0x01, 0x34, 0x12, 0x78, 0x61);
    Run(cpu, 3);
    Assert.That(cpu.BC, Is.EqualTo(0x1234));
    Assert.That(cpu.A, Is.EqualTo(0x12));
    Assert.That(cpu.H, Is.EqualTo(0x34));
  }

  [Test]
  public void LdHlIndirect_ReadWrite() {
    // LD HL,$2000 ; LD (HL),$AB ; LD A,(HL)
    var (cpu, bus) = Load(0x21, 0x00, 0x20, 0x36, 0xAB, 0x7E);
    Run(cpu, 3);
    Assert.That(bus.Ram[0x2000], Is.EqualTo(0xAB));
    Assert.That(cpu.A, Is.EqualTo(0xAB));
  }

  [Test]
  public void Add_SetsHalfCarryAndOverflow() {
    // LD A,$0F ; ADD A,$01 → $10, H set, no overflow.
    var (cpu, _) = Load(0x3E, 0x0F, 0xC6, 0x01);
    Run(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0x10));
    Assert.That(Flag(cpu, Cpu.Flags.H), Is.True);
    Assert.That(Flag(cpu, Cpu.Flags.PV), Is.False);
    Assert.That(Flag(cpu, Cpu.Flags.N), Is.False);
  }

  [Test]
  public void Add_SignedOverflow_SetsPv() {
    // LD A,$50 ; ADD A,$50 → $A0, overflow (pos+pos→neg).
    var (cpu, _) = Load(0x3E, 0x50, 0xC6, 0x50);
    Run(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0xA0));
    Assert.That(Flag(cpu, Cpu.Flags.PV), Is.True);
    Assert.That(Flag(cpu, Cpu.Flags.S), Is.True);
    Assert.That(Flag(cpu, Cpu.Flags.C), Is.False);
  }

  [Test]
  public void Adc_WithCarry_AddsOne() {
    // SCF ; LD A,$10 ; ADC A,$20 → $31 (carry added).
    var (cpu, _) = Load(0x37, 0x3E, 0x10, 0xCE, 0x20);
    Run(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0x31));
  }

  [Test]
  public void Sub_SetsCarryBorrowAndN() {
    // LD A,$10 ; SUB $20 → $F0, carry (borrow) set, N set.
    var (cpu, _) = Load(0x3E, 0x10, 0xD6, 0x20);
    Run(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0xF0));
    Assert.That(Flag(cpu, Cpu.Flags.C), Is.True);
    Assert.That(Flag(cpu, Cpu.Flags.N), Is.True);
  }

  [Test]
  public void Cp_DoesNotStore_ButSetsZero() {
    // LD A,$42 ; CP $42 → A unchanged, Z set.
    var (cpu, _) = Load(0x3E, 0x42, 0xFE, 0x42);
    Run(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0x42));
    Assert.That(Flag(cpu, Cpu.Flags.Z), Is.True);
  }

  [Test]
  public void Daa_AdjustsBcdAfterAdd() {
    // LD A,$19 ; ADD A,$01 → $1A ; DAA → $20.
    var (cpu, _) = Load(0x3E, 0x19, 0xC6, 0x01, 0x27);
    Run(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0x20));
  }

  [Test]
  public void Daa_AdjustsBcdAfterSub() {
    // LD A,$20 ; SUB $01 → $1F ; DAA → $19 (N set path).
    var (cpu, _) = Load(0x3E, 0x20, 0xD6, 0x01, 0x27);
    Run(cpu, 3);
    Assert.That(cpu.A, Is.EqualTo(0x19));
  }

  [Test]
  public void Ldir_CopiesBlock() {
    // LD HL,$2000 ; LD DE,$3000 ; LD BC,$0004 ; LDIR
    var (cpu, bus) = Load(0x21, 0x00, 0x20, 0x11, 0x00, 0x30, 0x01, 0x04, 0x00, 0xED, 0xB0);
    bus.Ram[0x2000] = 0x11; bus.Ram[0x2001] = 0x22; bus.Ram[0x2002] = 0x33; bus.Ram[0x2003] = 0x44;
    // 3 setup loads, then LDIR repeats until BC==0.
    Run(cpu, 3);
    var guard = 0;
    while (cpu.BC != 0 && guard++ < 100) cpu.Step();
    Assert.That(bus.Ram[0x3000], Is.EqualTo(0x11));
    Assert.That(bus.Ram[0x3003], Is.EqualTo(0x44));
    Assert.That(cpu.BC, Is.EqualTo(0));
    Assert.That(cpu.HL, Is.EqualTo(0x2004));
    Assert.That(cpu.DE, Is.EqualTo(0x3004));
  }

  [Test]
  public void Otir_WritesBlockToPort() {
    // LD HL,$2000 ; LD B,$03 ; LD C,$10 ; OTIR
    var (cpu, bus) = Load(0x21, 0x00, 0x20, 0x06, 0x03, 0x0E, 0x10, 0xED, 0xB3);
    bus.Ram[0x2000] = 0xAA; bus.Ram[0x2001] = 0xBB; bus.Ram[0x2002] = 0xCC;
    Run(cpu, 3);
    var guard = 0;
    while (cpu.B != 0 && guard++ < 100) cpu.Step();
    Assert.That(bus.OutLog.Count, Is.EqualTo(3));
    Assert.That(bus.OutLog[0].Value, Is.EqualTo(0xAA));
    Assert.That(bus.OutLog[2].Value, Is.EqualTo(0xCC));
  }

  [Test]
  public void Adc16_SetsFlags() {
    // LD HL,$0FFF ; LD BC,$0001 ; AND A (clear carry) ; ADC HL,BC → $1000, H set.
    var (cpu, _) = Load(0x21, 0xFF, 0x0F, 0x01, 0x01, 0x00, 0xA7, 0xED, 0x4A);
    Run(cpu, 4);
    Assert.That(cpu.HL, Is.EqualTo(0x1000));
    Assert.That(Flag(cpu, Cpu.Flags.H), Is.True);
  }

  [Test]
  public void Sbc16_Underflow_SetsCarry() {
    // LD HL,$0000 ; LD BC,$0001 ; AND A ; SBC HL,BC → $FFFF, carry set.
    var (cpu, _) = Load(0x21, 0x00, 0x00, 0x01, 0x01, 0x00, 0xA7, 0xED, 0x42);
    Run(cpu, 4);
    Assert.That(cpu.HL, Is.EqualTo(0xFFFF));
    Assert.That(Flag(cpu, Cpu.Flags.C), Is.True);
    Assert.That(Flag(cpu, Cpu.Flags.N), Is.True);
  }

  [Test]
  public void IxDisplacement_ReadAndWrite() {
    // LD IX,$2000 ; LD (IX+2),$77 ; LD A,(IX+2)
    var (cpu, bus) = Load(0xDD, 0x21, 0x00, 0x20, 0xDD, 0x36, 0x02, 0x77, 0xDD, 0x7E, 0x02);
    Run(cpu, 3);
    Assert.That(bus.Ram[0x2002], Is.EqualTo(0x77));
    Assert.That(cpu.A, Is.EqualTo(0x77));
  }

  [Test]
  public void IxNegativeDisplacement() {
    // LD IX,$2005 ; LD A,(IX-5) where (IX-5)=$2000 holds $5A.
    var (cpu, bus) = Load(0xDD, 0x21, 0x05, 0x20, 0xDD, 0x7E, 0xFB); // FB = -5
    bus.Ram[0x2000] = 0x5A;
    Run(cpu, 2);
    Assert.That(cpu.A, Is.EqualTo(0x5A));
  }

  [Test]
  public void DdCb_BitSetOnIndexed() {
    // LD IX,$2000 ; SET 3,(IX+0)
    var (cpu, bus) = Load(0xDD, 0x21, 0x00, 0x20, 0xDD, 0xCB, 0x00, 0xDE); // DE = SET 3,(IX+d)
    Run(cpu, 2);
    Assert.That(bus.Ram[0x2000] & 0x08, Is.EqualTo(0x08));
  }

  [Test]
  public void Exx_SwapsRegisterSet() {
    // LD BC,$1111 ; EXX ; LD BC,$2222 ; EXX → BC back to $1111.
    var (cpu, _) = Load(0x01, 0x11, 0x11, 0xD9, 0x01, 0x22, 0x22, 0xD9);
    Run(cpu, 4);
    Assert.That(cpu.BC, Is.EqualTo(0x1111));
  }

  [Test]
  public void ExAfAf_SwapsAccumulatorAndFlags() {
    // LD A,$55 ; EX AF,AF' ; LD A,$AA ; EX AF,AF' → A back to $55.
    var (cpu, _) = Load(0x3E, 0x55, 0x08, 0x3E, 0xAA, 0x08);
    Run(cpu, 4);
    Assert.That(cpu.A, Is.EqualTo(0x55));
  }

  [Test]
  public void Jr_Forward_Branches() {
    // JR +2 over a LD A,$FF, then LD A,$01.
    var (cpu, _) = Load(0x18, 0x02, 0x3E, 0xFF, 0x3E, 0x01);
    cpu.Step();            // JR +2 → skips the LD A,$FF
    Assert.That(cpu.PC, Is.EqualTo(0x0104));
    cpu.Step();            // LD A,$01
    Assert.That(cpu.A, Is.EqualTo(0x01));
  }

  [Test]
  public void Djnz_LoopsUntilBZero() {
    // LD B,$03 ; (loop) DEC C ; DJNZ loop → C decremented 3 times.
    var (cpu, _) = Load(0x06, 0x03, 0x0D, 0x10, 0xFD); // FD = -3
    cpu.C = 0x10;
    Run(cpu, 1); // LD B,$03
    var guard = 0;
    while (cpu.B != 0 && guard++ < 50) { cpu.Step(); cpu.Step(); }
    Assert.That(cpu.C, Is.EqualTo(0x0D));
    Assert.That(cpu.B, Is.EqualTo(0));
  }

  [Test]
  public void CallAndRet_RoundTrip() {
    // $0100: CALL $0110 ; $0103: LD A,$11
    // $0110: LD A,$22 ; RET
    var (cpu, bus) = Load(0xCD, 0x10, 0x01, 0x3E, 0x11);
    bus.Ram[0x0110] = 0x3E; bus.Ram[0x0111] = 0x22; bus.Ram[0x0112] = 0xC9;
    cpu.Step(); // CALL
    Assert.That(cpu.PC, Is.EqualTo(0x0110));
    cpu.Step(); // LD A,$22
    cpu.Step(); // RET
    Assert.That(cpu.PC, Is.EqualTo(0x0103));
    Assert.That(cpu.A, Is.EqualTo(0x22));
  }

  [Test]
  public void Rst_PushesAndJumps() {
    // RST $18 → pushes return, PC=$0018.
    var (cpu, _) = Load(0xDF);
    var sp = cpu.SP;
    cpu.Step();
    Assert.That(cpu.PC, Is.EqualTo(0x0018));
    Assert.That(cpu.SP, Is.EqualTo((ushort)(sp - 2)));
  }

  [Test]
  public void RunUntilRet_StopsAtMatchingReturn() {
    // Subroutine at $0300: LD A,$AA ; RET.
    var bus = new RamBus();
    bus.Ram[0x0300] = 0x3E; bus.Ram[0x0301] = 0xAA; bus.Ram[0x0302] = 0xC9;
    var cpu = new Cpu(bus) { SP = 0xFFF0 };
    var cycles = cpu.RunUntilRet(0x0300, 1000);
    Assert.That(cpu.A, Is.EqualTo(0xAA));
    Assert.That(cycles, Is.GreaterThan(0));
  }

  [Test]
  public void Im2_VectorDispatch() {
    // IM 2 ; EI ; set I=$80 so the vector table is at $80xx. Device puts $40 on the bus,
    // so the pointer is $8040 → handler address there.
    var bus = new RamBus();
    bus.Ram[0x0100] = 0xED; bus.Ram[0x0101] = 0x5E; // IM 2
    bus.Ram[0x0102] = 0xFB;                         // EI
    bus.Ram[0x8040] = 0x00; bus.Ram[0x8041] = 0x40; // vector → $4000
    var cpu = new Cpu(bus) { PC = 0x0100, SP = 0xFFF0, I = 0x80 };
    cpu.Step(); // IM 2
    cpu.Step(); // EI
    var cycles = cpu.RaiseIrq(0x40);
    Assert.That(cpu.PC, Is.EqualTo(0x4000));
    Assert.That(cpu.IFF1, Is.False, "IRQ acknowledge clears IFF1");
    Assert.That(cycles, Is.EqualTo(19));
  }

  [Test]
  public void Im1_VectorsTo0038() {
    var bus = new RamBus();
    bus.Ram[0x0100] = 0xED; bus.Ram[0x0101] = 0x56; // IM 1
    bus.Ram[0x0102] = 0xFB;                         // EI
    var cpu = new Cpu(bus) { PC = 0x0100, SP = 0xFFF0 };
    cpu.Step(); cpu.Step();
    cpu.RaiseIrq(0x00);
    Assert.That(cpu.PC, Is.EqualTo(0x0038));
  }

  [Test]
  public void RaiseIrq_MaskedWhenDisabled() {
    var bus = new RamBus();
    bus.Ram[0x0100] = 0xF3; // DI
    var cpu = new Cpu(bus) { PC = 0x0100, SP = 0xFFF0 };
    cpu.Step();
    var cycles = cpu.RaiseIrq(0xFF);
    Assert.That(cycles, Is.EqualTo(0));
    Assert.That(cpu.PC, Is.EqualTo(0x0101));
  }

  [Test]
  public void OutN_PlacesAccumulatorOnHighAddressByte() {
    // LD A,$5A ; OUT ($10),A → port = (A<<8)|n = $5A10, value $5A.
    var (cpu, bus) = Load(0x3E, 0x5A, 0xD3, 0x10);
    Run(cpu, 2);
    Assert.That(bus.OutLog, Has.Count.EqualTo(1));
    Assert.That(bus.OutLog[0].Port, Is.EqualTo(0x5A10));
    Assert.That(bus.OutLog[0].Value, Is.EqualTo(0x5A));
  }

  [Test]
  public void OutC_UsesBcAsPort() {
    // LD BC,$A05A ; LD A,$77 ; OUT (C),A → port BC=$A05A, value $77.
    var (cpu, bus) = Load(0x01, 0x5A, 0xA0, 0x3E, 0x77, 0xED, 0x79);
    Run(cpu, 3);
    Assert.That(bus.OutLog[0].Port, Is.EqualTo(0xA05A));
    Assert.That(bus.OutLog[0].Value, Is.EqualTo(0x77));
  }
}
