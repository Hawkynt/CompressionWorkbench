#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Nes2a03;

namespace Compression.Tests.Codecs.Nes2a03;

[TestFixture]
public class NsfPlayerTests {

  private const double NtscClock = 1789773.0;
  private const int Rate = 44100;

  // Builds a NESM v1 file with a hand-assembled init/play program loaded at $8000.
  private static byte[] BuildNesm(byte[] program, byte chipFlags = 0x00, byte palNtsc = 0x00,
      ushort loadAddr = 0x8000, ushort initAddr = 0x8000, ushort playAddr = 0x8100,
      byte[]? banks = null, ushort ntscSpeed = 0) {
    var blob = new byte[0x80 + program.Length];
    "NESM\x1A"u8.CopyTo(blob);
    blob[0x05] = 1;
    blob[0x06] = 1;            // total songs
    blob[0x07] = 1;            // start song
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x08), loadAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0A), initAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x0C), playAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x6E), ntscSpeed);
    if (banks is not null)
      banks.CopyTo(blob, 0x70);
    blob[0x7A] = palNtsc;
    blob[0x7B] = chipFlags;
    program.CopyTo(blob, 0x80);
    return blob;
  }

  // init: enable pulse 1 with a fixed timer, 50% duty, constant volume, length load, then RTS.
  // play: bare RTS at offset 0x100.
  private static byte[] ToneProgram(int timer) {
    var p = new List<byte>();
    void Sta(byte lo, byte hi, byte value) {
      p.Add(0xA9); p.Add(value);          // LDA #value
      p.Add(0x8D); p.Add(lo); p.Add(hi);  // STA $hilo
    }
    Sta(0x15, 0x40, 0x01);                          // $4015 = enable pulse 1
    Sta(0x00, 0x40, 0xBF);                          // $4000 duty 50% + const vol 15 + halt
    Sta(0x02, 0x40, (byte)(timer & 0xFF));          // $4002 timer low
    Sta(0x03, 0x40, (byte)(((timer >> 8) & 0x07) | (0x10 << 3))); // $4003 timer high + length
    p.Add(0x60);                                    // RTS

    while (p.Count < 0x100) p.Add(0xEA);            // NOP pad to $8100
    p.Add(0x60);                                    // play: RTS
    return p.ToArray();
  }

  private static int TimerForFreq(double hz) => (int)Math.Round(NtscClock / (16.0 * hz) - 1.0);

  private static int ZeroCrossings(short[] s) {
    var mean = (int)s.Average(x => (double)x);
    var c = 0;
    for (var i = 1; i < s.Length; ++i)
      if (s[i - 1] - mean <= 0 && s[i] - mean > 0) ++c;
    return c;
  }

  [Test]
  public void Player_RendersPeriodicToneAtApuFrequency() {
    const double targetHz = 440.0;
    var blob = BuildNesm(ToneProgram(TimerForFreq(targetHz)));
    var player = NsfPlayer.FromNesm(blob);
    var samples = player.Render(0.5, Rate);

    var peak = samples.Max(x => Math.Abs((int)x));
    Assert.That(peak, Is.GreaterThan(1000), "render should not be silent");

    var slice = samples[^(Rate / 4)..];
    var crossings = ZeroCrossings(slice) * 4; // crossings per 0.25 s → Hz
    Assert.That(crossings, Is.EqualTo(440).Within(20), $"fundamental ~{crossings} Hz");
  }

  [Test]
  public void Player_NtscDefaultFrameRateIs60Hz() {
    var blob = BuildNesm(ToneProgram(TimerForFreq(440.0)));
    var player = NsfPlayer.FromNesm(blob);
    Assert.That(player.FrameRateHz, Is.EqualTo(60.0).Within(0.01));
  }

  [Test]
  public void Player_HonoursNtscSpeedWord() {
    // speed word in microseconds: 20000 µs → 50 calls/sec.
    var blob = BuildNesm(ToneProgram(TimerForFreq(440.0)), ntscSpeed: 20000);
    var player = NsfPlayer.FromNesm(blob);
    Assert.That(player.FrameRateHz, Is.EqualTo(50.0).Within(0.01));
  }

  [Test]
  public void Player_BankswitchedTuneRendersTone() {
    // Lay out a banked NSF: 4 KB banks. loadAddr low nibble 0 → bank base at offset 0. The
    // init program lives in bank 0 (mapped at $8000), play in the same bank. Bank registers
    // map banks 0..7 to slots 0..7.
    var program = ToneProgram(TimerForFreq(330.0));
    // Pad program to a couple of banks so bankswitched reads stay in range.
    var data = new byte[0x2000];
    program.CopyTo(data, 0);
    var banks = new byte[] { 0, 1, 0, 1, 0, 1, 0, 1 };

    var blob = BuildNesm(data, loadAddr: 0x8000, initAddr: 0x8000, playAddr: 0x8100, banks: banks);
    var player = NsfPlayer.FromNesm(blob);
    var samples = player.Render(0.3, Rate);

    var peak = samples.Max(x => Math.Abs((int)x));
    Assert.That(peak, Is.GreaterThan(1000), "bankswitched render should not be silent");
  }

  [Test]
  public void Player_ExpansionChipThrows() {
    var blob = BuildNesm(ToneProgram(TimerForFreq(440.0)), chipFlags: 0x01); // VRC6
    Assert.Throws<NotSupportedException>(() => NsfPlayer.FromNesm(blob));
  }

  [Test]
  public void Player_PalRegionUsesPalClockAndRate() {
    var blob = BuildNesm(ToneProgram(TimerForFreq(440.0)), palNtsc: 0x01); // PAL
    var player = NsfPlayer.FromNesm(blob);
    Assert.That(player.FrameRateHz, Is.EqualTo(50.0).Within(0.01));
  }
}
