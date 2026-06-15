#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Sid;

namespace Compression.Tests.Codecs.Sid;

[TestFixture]
public class MultiSidPlayerTests {

  private const double PalClock = 985248.0;

  // Builds a PSID file. The program is loaded at $1000; init at $1000, play (bare RTS) at $1040.
  private static byte[] BuildPsid(ushort version, ushort flags, byte[] program,
      byte secondSid = 0, byte thirdSid = 0, ushort initAddr = 0x1000, ushort playAddr = 0x1040) {
    const int header = 0x7C;
    var blob = new byte[header + program.Length];
    Encoding.ASCII.GetBytes("PSID").CopyTo(blob, 0);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x04), version);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x06), header);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x08), 0x1000);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0A), initAddr);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0C), playAddr);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x0E), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x10), 1);
    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0x76), flags);
    blob[0x7A] = secondSid;
    blob[0x7B] = thirdSid;
    program.CopyTo(blob, header);
    return blob;
  }

  // init: writes a saw tone into the SID register window at absolute base address `sidBase`,
  // then RTS. play (at $1040) is RTS. freqReg picks the pitch.
  private static byte[] ToneProgram(int freqReg, ushort sidBase) {
    var p = new List<byte>();
    void Set(byte reg, byte value) {
      var addr = (ushort)(sidBase + reg);
      p.Add(0xA9); p.Add(value);                 // LDA #value
      p.Add(0x8D); p.Add((byte)(addr & 0xFF)); p.Add((byte)(addr >> 8)); // STA addr
    }
    Set(0x00, (byte)(freqReg & 0xFF));
    Set(0x01, (byte)(freqReg >> 8));
    Set(0x06, 0xF0);
    Set(0x18, 0x0F);
    Set(0x04, 0x21);
    p.Add(0x60);
    while (p.Count < 0x40) p.Add(0xEA);
    p.Add(0x60);
    return p.ToArray();
  }

  private static int Peak(short[] s) => s.Length == 0 ? 0 : s.Max(x => Math.Abs((int)x));

  [Test]
  public void Mono_SingleChipRendersOneBuffer() {
    var freq = (int)Math.Round(440.0 * 16777216.0 / PalClock);
    var blob = BuildPsid(2, 0x0014, ToneProgram(freq, 0xD400));
    var player = new PsidPlayer(blob, SidModel.Mos6581, PalClock);
    Assert.That(player.SidCount, Is.EqualTo(1));
    var per = player.RenderPerChip(0.4);
    Assert.That(per.Length, Is.EqualTo(1));
    Assert.That(Peak(per[0]), Is.GreaterThan(1000));
  }

  [Test]
  public void TwoSid_ToneOnSid2_LeavesSid1SilentAndSid2Loud() {
    // Init writes the tone to SID #2's window ($D420). SID #1 must stay silent.
    var freq = (int)Math.Round(440.0 * 16777216.0 / PalClock);
    var blob = BuildPsid(3, 0x0014, ToneProgram(freq, 0xD420), secondSid: 0x42);
    var chips = new[] {
      new SidChipConfig(0xD400, SidModel.Mos6581),
      new SidChipConfig(0xD420, SidModel.Mos6581),
    };
    var player = new PsidPlayer(blob, chips, PalClock);
    Assert.That(player.SidCount, Is.EqualTo(2));
    var per = player.RenderPerChip(0.4);

    Assert.That(Peak(per[0]), Is.LessThan(50), "SID #1 must receive no writes");
    Assert.That(Peak(per[1]), Is.GreaterThan(1000), "SID #2 should carry the tone");
  }

  [Test]
  public void Bus_WritesToSid2WindowNeverHitSid1() {
    // Write only to $D420-$D43F; SID #1 ($D400 window) must be untouched.
    var freq = (int)Math.Round(440.0 * 16777216.0 / PalClock);
    var blob = BuildPsid(3, 0x0014, ToneProgram(freq, 0xD420), secondSid: 0x42);
    var chips = new[] {
      new SidChipConfig(0xD400, SidModel.Mos6581),
      new SidChipConfig(0xD420, SidModel.Mos8580),
    };
    var player = new PsidPlayer(blob, chips, PalClock);
    var per = player.RenderPerChip(0.3);
    Assert.That(Peak(per[0]), Is.EqualTo(0).Within(10), "no write reached SID #1");
  }

  [Test]
  public void ThreeSid_ToneOnSid3RoutesToThirdBufferOnly() {
    var freq = (int)Math.Round(440.0 * 16777216.0 / PalClock);
    // SID #3 at $D440 (byte 0x44). Write the tone there.
    var blob = BuildPsid(4, 0x0014, ToneProgram(freq, 0xD440), secondSid: 0x42, thirdSid: 0x44);
    var chips = new[] {
      new SidChipConfig(0xD400, SidModel.Mos6581),
      new SidChipConfig(0xD420, SidModel.Mos6581),
      new SidChipConfig(0xD440, SidModel.Mos6581),
    };
    var player = new PsidPlayer(blob, chips, PalClock);
    Assert.That(player.SidCount, Is.EqualTo(3));
    var per = player.RenderPerChip(0.4);
    Assert.That(Peak(per[0]), Is.LessThan(50));
    Assert.That(Peak(per[1]), Is.LessThan(50));
    Assert.That(Peak(per[2]), Is.GreaterThan(1000));
  }

  [Test]
  public void PerChipModels_DifferentModelsAreReportedAndApplied() {
    var blob = BuildPsid(3, 0x0014, ToneProgram(1000, 0xD400), secondSid: 0x42);
    var chips = new[] {
      new SidChipConfig(0xD400, SidModel.Mos6581),
      new SidChipConfig(0xD420, SidModel.Mos8580),
    };
    var player = new PsidPlayer(blob, chips, PalClock);
    Assert.That(player.ModelOf(0), Is.EqualTo(SidModel.Mos6581));
    Assert.That(player.ModelOf(1), Is.EqualTo(SidModel.Mos8580));
  }

  [Test]
  public void Mos6582_BehavesAsMos8580() {
    var chip6582 = new SidChip(SidModel.Mos6582, PalClock);
    var chip8580 = new SidChip(SidModel.Mos8580, PalClock);
    Assert.That(chip6582.Model, Is.EqualTo(SidModel.Mos8580));
    Assert.That(chip8580.Model, Is.EqualTo(SidModel.Mos8580));
    Assert.That(SidModel.Mos6582.Resolve(), Is.EqualTo(SidModel.Mos8580));
    Assert.That(SidModel.Mos6581.Resolve(), Is.EqualTo(SidModel.Mos6581));
  }
}
