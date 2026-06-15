#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.GameBoyApu;

namespace Compression.Tests.Codecs.GbApu;

[TestFixture]
public class GbsPlayerTests {

  private const int HeaderSize = 0x70;

  // Builds a GBS file with the given program loaded at loadAddr and the supplied vectors.
  private static byte[] BuildGbs(byte[] program, ushort loadAddr, ushort initAddr, ushort playAddr,
      ushort stackPtr = 0xFFFE, byte timerModulo = 0, byte timerControl = 0, byte firstSong = 1) {
    var blob = new byte[HeaderSize + program.Length];
    blob[0] = 0x47; blob[1] = 0x42; blob[2] = 0x53; blob[3] = 1; // "GBS" v1
    blob[0x04] = 1;            // num songs
    blob[0x05] = firstSong;
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x06), loadAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), initAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), playAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0C), stackPtr);
    blob[0x0E] = timerModulo;
    blob[0x0F] = timerControl;
    program.CopyTo(blob, HeaderSize);
    return blob;
  }

  // init routine: route CH2 to both sides, set up a ~440 Hz tone at full volume, then RET.
  // play routine: RET only. The whole thing is loaded at $0400 (loadAddr).
  private static (byte[] Program, ushort LoadAddr, ushort InitAddr, ushort PlayAddr) BuildTone(int freqValue) {
    const ushort load = 0x0400;
    var p = new List<byte>();

    // Helper: LD A,#v ; LDH ($reg),A — write APU register $FF00+reg.
    void WriteReg(byte reg, byte value) {
      p.Add(0x3E); p.Add(value); // LD A,value
      p.Add(0xE0); p.Add(reg);   // LDH (reg),A
    }

    // NR52 power on is done by the player; route + voice setup here.
    WriteReg(0x25, 0x22);                         // NR51: CH2 both sides
    WriteReg(0x16, 0x80);                         // NR21 duty 2
    WriteReg(0x17, 0xF0);                         // NR22 vol 15, no decay
    WriteReg(0x18, (byte)(freqValue & 0xFF));     // NR23 freq lo
    WriteReg(0x19, (byte)(0x80 | ((freqValue >> 8) & 0x07))); // NR24 trigger + hi
    p.Add(0xC9);                                  // RET (end of init)

    var playOffset = p.Count;
    p.Add(0xC9);                                  // play: RET

    return (p.ToArray(), load, load, (ushort)(load + playOffset));
  }

  [Test]
  public void RendersToneWithStereoRouting() {
    var freqValue = (int)Math.Round(2048 - 131072.0 / 440.0);
    var (program, load, init, play) = BuildTone(freqValue);
    var blob = BuildGbs(program, load, init, play);

    var player = new GbsPlayer(blob, song: 0);
    var stereo = player.Render(0.5);

    var frames = stereo.Length / 2;
    var left = new short[frames]; var right = new short[frames];
    for (var f = 0; f < frames; ++f) { left[f] = stereo[f * 2]; right[f] = stereo[f * 2 + 1]; }

    Assert.That(left.Max(s => Math.Abs((int)s)), Is.GreaterThan(500), "left audible");
    Assert.That(right.Max(s => Math.Abs((int)s)), Is.GreaterThan(500), "right audible (CH2 routed both)");

    // Fundamental ~440 Hz via zero crossings.
    double mean = left.Sum(s => (double)s) / left.Length;
    var crossings = 0;
    for (var i = 1; i < left.Length; ++i)
      if (left[i - 1] - mean <= 0 && left[i] - mean > 0) ++crossings;
    var hz = crossings / 0.5;
    Assert.That(hz, Is.EqualTo(440).Within(30), $"measured {hz} Hz");
  }

  [Test]
  public void DefaultVblankRate_IsAbout59Hz() {
    var (program, load, init, play) = BuildTone(1000);
    var blob = BuildGbs(program, load, init, play); // timerControl 0 → VBlank
    var player = new GbsPlayer(blob, song: 0);
    Assert.That(player.FrameRateHz, Is.EqualTo(59.7).Within(0.5));
  }

  [Test]
  public void TimerDrivenRate_HonoursModuloAndControl() {
    // timerControl bit 2 set, low bits 00 → base 4096 Hz; modulo 0 → divisor 256 → 16 Hz.
    var (program, load, init, play) = BuildTone(1000);
    var blob = BuildGbs(program, load, init, play, timerModulo: 0x00, timerControl: 0x04);
    var player = new GbsPlayer(blob, song: 0);
    Assert.That(player.FrameRateHz, Is.EqualTo(4096.0 / 256).Within(0.01)); // 16 Hz
  }

  [Test]
  public void TimerDrivenRate_FastBaseFrequency() {
    // control bits: bit2 set + base select 11 → 16384 Hz; modulo 0xC0 → divisor 64 → 256 Hz.
    var (program, load, init, play) = BuildTone(1000);
    var blob = BuildGbs(program, load, init, play, timerModulo: 0xC0, timerControl: 0x07);
    var player = new GbsPlayer(blob, song: 0);
    Assert.That(player.FrameRateHz, Is.EqualTo(16384.0 / 64).Within(0.01)); // 256 Hz
  }

  [Test]
  public void BankedGbs_ReadsFromSelectedRomBank() {
    // The init routine lives in bank 0 (at loadAddr $0000). It selects ROM bank 2 (write
    // $2000), then reads a marker byte from $4000 (start of the banked window) which must come
    // from the third 16 KB chunk of GBS data, and writes it to NR22 as the CH2 volume nibble.
    const ushort load = 0x0000;
    var init = new List<byte>();
    void WriteReg(byte reg, byte value) { init.Add(0x3E); init.Add(value); init.Add(0xE0); init.Add(reg); }

    WriteReg(0x25, 0x22);          // NR51 both sides
    WriteReg(0x16, 0x80);          // NR21 duty
    // Select bank 2.
    init.Add(0x3E); init.Add(0x02);             // LD A,2
    init.Add(0xEA); init.Add(0x00); init.Add(0x20); // LD ($2000),A  (bank select)
    // Read marker from $4000 into A.
    init.Add(0xFA); init.Add(0x00); init.Add(0x40); // LD A,($4000)
    init.Add(0xE0); init.Add(0x17);             // LDH ($17),A → NR22 = marker (0xF0)
    // Frequency + trigger.
    var freqValue = (int)Math.Round(2048 - 131072.0 / 440.0);
    WriteReg(0x18, (byte)(freqValue & 0xFF));
    WriteReg(0x19, (byte)(0x80 | ((freqValue >> 8) & 0x07)));
    init.Add(0xC9); // RET
    var playOffset = init.Count;
    init.Add(0xC9); // play RET

    // Build the GBS data: 3 banks of 16 KB. Bank 0 holds the init program; bank 2 holds the
    // marker byte 0xF0 at its very start (data index 2*0x4000).
    var data = new byte[3 * 0x4000];
    init.ToArray().CopyTo(data, 0);
    data[2 * 0x4000] = 0xF0; // marker → full-volume envelope

    var blob = BuildGbs(data, load, load, (ushort)(load + playOffset));
    var player = new GbsPlayer(blob, song: 0);
    var stereo = player.Render(0.3);
    var peak = 0;
    for (var i = 0; i < stereo.Length; ++i) peak = Math.Max(peak, Math.Abs(stereo[i]));
    Assert.That(peak, Is.GreaterThan(500), "banked marker should set CH2 volume → audible tone");
  }

  [Test]
  public void MissingMagic_Throws() {
    var blob = new byte[HeaderSize];
    Assert.Throws<NotSupportedException>(() => new GbsPlayer(blob, song: 0));
  }
}
