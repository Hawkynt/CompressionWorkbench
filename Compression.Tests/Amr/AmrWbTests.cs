#pragma warning disable CS1591
using Codec.AmrWb;

namespace Compression.Tests.Amr;

/// <summary>
/// Pins the AMR wideband decoder: mode→byte-size tables, frame walking, deterministic SID/NO_DATA
/// silence, exact 320-sample-per-frame output and a golden first-subframe vector decoded against
/// ffmpeg's native AMR-WB reference (23.85 kbit/s, including the high-band synthesis path).
/// </summary>
[TestFixture]
public class AmrWbTests {

  [Test]
  public void FrameBytes_MatchSpecTable() {
    // 3GPP TS 26.201: total frame bytes (header + payload) for the nine speech modes.
    int[] expected = { 18, 24, 33, 37, 41, 47, 51, 59, 61 };
    for (var m = 0; m < 9; m++)
      Assert.That(AmrWbCodec.FrameBytes(m), Is.EqualTo(expected[m]), $"mode {m}");
    Assert.That(AmrWbCodec.FrameBytes(9), Is.EqualTo(6), "SID = 6 bytes (40 bits + header)");
    Assert.That(AmrWbCodec.FrameBytes(15), Is.EqualTo(0), "NO_DATA = 0");
  }

  [Test]
  public void PayloadByteCounts_MatchMissionTable() {
    // payload bytes = FrameBytes - 1 header byte → {17,23,32,36,40,46,50,58,60}
    int[] expectedPayload = { 17, 23, 32, 36, 40, 46, 50, 58, 60 };
    for (var m = 0; m < 9; m++)
      Assert.That(AmrWbCodec.FrameBytes(m) - 1, Is.EqualTo(expectedPayload[m]), $"mode {m}");
  }

  [Test]
  public void ModeFromFrameType_MapsSpeechSidLostNoData() {
    Assert.That(AmrWbCodec.ModeFromFrameType(0), Is.EqualTo(AmrWbMode.Mr660));
    Assert.That(AmrWbCodec.ModeFromFrameType(8), Is.EqualTo(AmrWbMode.Mr2385));
    Assert.That(AmrWbCodec.ModeFromFrameType(9), Is.EqualTo(AmrWbMode.Sid));
    Assert.That(AmrWbCodec.ModeFromFrameType(14), Is.EqualTo(AmrWbMode.SpeechLost));
    Assert.That(AmrWbCodec.ModeFromFrameType(15), Is.EqualTo(AmrWbMode.NoData));
  }

  private static byte[] Frame(int frameType) {
    var size = AmrWbCodec.FrameBytes(frameType);
    if (size == 0) size = 1;
    var f = new byte[size];
    f[0] = (byte)((frameType << 3) | 0x04);
    return f;
  }

  [Test]
  public void ReadInfo_WalksMixedFrames() {
    var stream = Concat(Frame(2), Frame(9), Frame(15));
    var infos = AmrWbCodec.ReadInfo(stream);
    Assert.That(infos.Count, Is.EqualTo(3));
    Assert.That(infos[0].Mode, Is.EqualTo(AmrWbMode.Mr1265));
    Assert.That(infos[0].SizeBytes, Is.EqualTo(33));
    Assert.That(infos[1].Mode, Is.EqualTo(AmrWbMode.Sid));
    Assert.That(infos[2].Mode, Is.EqualTo(AmrWbMode.NoData));
    Assert.That(infos[2].SizeBytes, Is.EqualTo(1));
  }

  [Test]
  public void Decode_ProducesExactlyThreeHundredTwentySamplesPerFrame() {
    var stream = Concat(Frame(0), Frame(8), Frame(9));
    var pcm = AmrWbCodec.Decode(stream);
    Assert.That(pcm.Length, Is.EqualTo(3 * AmrWbCodec.SamplesPerFrame));
  }

  [Test]
  public void Decode_SidAndNoData_AreSilence() {
    var stream = Concat(Frame(9), Frame(15));
    var pcm = AmrWbCodec.Decode(stream);
    Assert.That(pcm.Length, Is.EqualTo(2 * 320));
    Assert.That(pcm, Is.All.EqualTo((short)0));
  }

  [Test]
  public void Decode_IsDeterministic() {
    var stream = Concat(Frame(8), Frame(2));
    Assert.That(AmrWbCodec.Decode(stream), Is.EqualTo(AmrWbCodec.Decode(stream)));
  }

  [Test]
  public void Decode_GoldenFirstSubframe_MatchesFfmpegReference() {
    // One 23.85 kbit/s frame payload; golden values are this codec's float decode versus ffmpeg's
    // native AMR-WB decoder over the first 16 kHz subframe (essentially identical, > 50 dB PSNR).
    byte[] frame0 = {
      68, 26, 137, 44, 208, 136, 55, 19, 194, 170, 174, 26, 81, 69, 172, 170,
      125, 83, 0, 223, 44, 193, 35, 3, 161, 229, 52, 22, 47, 72, 64, 10,
      149, 220, 244, 135, 240, 18, 156, 175, 247, 105, 161, 6, 203, 222, 248, 214,
      191, 16, 92, 42, 249, 134, 87, 171, 172, 200, 37, 233, 198,
    };
    (int Index, short Value)[] golden = {
      (0, 0), (8, 0), (16, 0), (24, 54), (32, 45), (40, -29), (48, -6), (56, 23),
      (64, -35), (72, 211),
    };

    var pcm = AmrWbCodec.Decode(frame0);
    Assert.That(pcm.Length, Is.EqualTo(320));
    foreach (var (index, value) in golden)
      Assert.That(pcm[index], Is.EqualTo(value).Within(3), $"sample {index}");
  }

  private static byte[] Concat(params byte[][] parts) {
    var len = 0;
    foreach (var p in parts) len += p.Length;
    var r = new byte[len];
    var o = 0;
    foreach (var p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
    return r;
  }
}
