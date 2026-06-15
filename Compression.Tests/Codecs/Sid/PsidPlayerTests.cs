#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Sid;

namespace Compression.Tests.Codecs.Sid;

[TestFixture]
public class PsidPlayerTests {

  private const double PalClock = 985248.0;

  // Builds a minimal PSID v2 file with a hand-assembled init/play program loaded at $1000.
  // init sets up voice 1 (freq + saw + sustain full + gate), play is a bare RTS.
  private static byte[] BuildPsid(string magic, ushort flags, byte[] program, ushort loadAddr = 0x1000,
      ushort initAddr = 0x1000, ushort playAddr = 0x1040, uint speed = 0) {
    const int header = 0x7C;
    var blob = new byte[header + program.Length];
    Encoding.ASCII.GetBytes(magic).CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), 2);          // version
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), header);     // dataOffset
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), loadAddr);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), initAddr);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), playAddr);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);          // songs
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);          // startSong
    BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(0x12), speed);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    program.CopyTo(blob, header);
    return blob;
  }

  // init routine: writes voice-1 registers via STA $D4xx, then RTS. play: RTS only at $1040.
  private static byte[] InitProgram(int freqReg) {
    var p = new List<byte>();
    // The init body assumes the SID base $D400. Each register set is LDA #imm ; STA $D4nn.
    void Set(byte reg, byte value) {
      p.Add(0xA9); p.Add(value);          // LDA #value
      p.Add(0x8D); p.Add(reg); p.Add(0xD4); // STA $D4nn
    }
    Set(0x00, (byte)(freqReg & 0xFF));   // freq lo
    Set(0x01, (byte)(freqReg >> 8));     // freq hi
    Set(0x06, 0xF0);                     // sustain full / release 0
    Set(0x18, 0x0F);                     // volume full
    Set(0x04, 0x21);                     // saw + gate
    p.Add(0x60);                         // RTS

    // Pad to $1040 (offset 0x40) then place the play RTS.
    while (p.Count < 0x40) p.Add(0xEA);  // NOP padding
    p.Add(0x60);                         // play: RTS
    return p.ToArray();
  }

  [Test]
  public void Psid_RendersPeriodicNonSilentWaveform() {
    var freqReg = (int)Math.Round(440.0 * 16777216.0 / PalClock);
    var blob = BuildPsid("PSID", flags: 0x0004, InitProgram(freqReg)); // clock PAL
    var player = new PsidPlayer(blob, SidModel.Mos6581, PalClock);
    var samples = player.Render(0.5, SidChip.OutputSampleRate);

    var peak = samples.Max(s => Math.Abs((int)s));
    Assert.That(peak, Is.GreaterThan(1000), "render should not be silent");

    // Fundamental check: count upward zero crossings over the last ~quarter second.
    var slice = samples[^(SidChip.OutputSampleRate / 4)..];
    var crossings = 0;
    for (var i = 1; i < slice.Length; ++i)
      if (slice[i - 1] <= 0 && slice[i] > 0) ++crossings;
    var hz = crossings * 4.0; // crossings over 0.25 s → Hz
    Assert.That(hz, Is.EqualTo(440).Within(25), $"fundamental ~{hz} Hz");
  }

  [Test]
  public void CiaSpeed_HonoursTimerWrittenDuringInit() {
    // init writes a CIA timer-A value, then sets up the voice. speed bit 0 selects CIA.
    var freqReg = (int)Math.Round(440.0 * 16777216.0 / PalClock);
    var p = new List<byte>();
    // STA timer: LDA #lo ; STA $DC04 ; LDA #hi ; STA $DC05.
    const int timer = 19710; // ~50 Hz at PAL clock (985248/19710 ≈ 50)
    p.Add(0xA9); p.Add(timer & 0xFF); p.Add(0x8D); p.Add(0x04); p.Add(0xDC);
    p.Add(0xA9); p.Add((timer >> 8) & 0xFF); p.Add(0x8D); p.Add(0x05); p.Add(0xDC);
    // voice setup
    void Set(byte reg, byte value) { p.Add(0xA9); p.Add(value); p.Add(0x8D); p.Add(reg); p.Add(0xD4); }
    Set(0x00, (byte)(freqReg & 0xFF));
    Set(0x01, (byte)(freqReg >> 8));
    Set(0x06, 0xF0); Set(0x18, 0x0F); Set(0x04, 0x21);
    p.Add(0x60);
    while (p.Count < 0x40) p.Add(0xEA);
    p.Add(0x60); // play RTS

    var blob = BuildPsid("PSID", flags: 0x0004, p.ToArray(), speed: 0x00000001); // CIA timing
    var player = new PsidPlayer(blob, SidModel.Mos6581, PalClock);
    Assert.That(player.FrameRateHz, Is.EqualTo(PalClock / timer).Within(0.5));
  }

  [Test]
  public void Rsid_ThrowsNotSupported() {
    var blob = BuildPsid("RSID", flags: 0x0004, InitProgram(1000));
    Assert.Throws<NotSupportedException>(() => new PsidPlayer(blob, SidModel.Mos6581, PalClock));
  }

  [Test]
  public void PsidBasicFlag_ThrowsNotSupported() {
    var blob = BuildPsid("PSID", flags: 0x0006, InitProgram(1000)); // bit1 = BASIC
    Assert.Throws<NotSupportedException>(() => new PsidPlayer(blob, SidModel.Mos6581, PalClock));
  }

  [Test]
  public void DefaultVblank_PalIs50Hz() {
    var blob = BuildPsid("PSID", flags: 0x0004, InitProgram(1000)); // clock PAL, speed 0 (vblank)
    var player = new PsidPlayer(blob, SidModel.Mos6581, PalClock);
    Assert.That(player.FrameRateHz, Is.EqualTo(50.0).Within(0.01));
  }
}
