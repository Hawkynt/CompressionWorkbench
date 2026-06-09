#pragma warning disable CS1591
using Codec.AmrNb;

namespace Compression.Tests.Amr;

/// <summary>
/// Pins the AMR narrowband decoder: mode→byte-size tables, frame walking, deterministic
/// SID/NO_DATA silence, exact 160-sample-per-frame output and a golden first-frame vector decoded
/// against ffmpeg's AMR-NB reference (12.2 kbit/s).
/// </summary>
[TestFixture]
public class AmrNbTests {

  [Test]
  public void PayloadBytes_MatchSpecTable() {
    // 3GPP TS 26.101 payload bytes for the eight speech modes + SID.
    int[] expected = { 12, 13, 15, 17, 19, 20, 26, 31 };
    for (var m = 0; m < 8; m++)
      Assert.That(AmrNbCodec.PayloadBytes(m), Is.EqualTo(expected[m]), $"mode {m}");
    Assert.That(AmrNbCodec.PayloadBytes(8), Is.EqualTo(5), "SID = 5 bytes");
    Assert.That(AmrNbCodec.PayloadBytes(15), Is.EqualTo(0), "NO_DATA = 0 payload");
  }

  [Test]
  public void ModeFromFrameType_MapsSpeechSidAndNoData() {
    Assert.That(AmrNbCodec.ModeFromFrameType(0), Is.EqualTo(AmrNbMode.Mr475));
    Assert.That(AmrNbCodec.ModeFromFrameType(7), Is.EqualTo(AmrNbMode.Mr122));
    Assert.That(AmrNbCodec.ModeFromFrameType(8), Is.EqualTo(AmrNbMode.MrdtxSid));
    Assert.That(AmrNbCodec.ModeFromFrameType(15), Is.EqualTo(AmrNbMode.NoData));
    Assert.That(AmrNbCodec.ModeFromFrameType(10), Is.EqualTo(AmrNbMode.NoData));
  }

  private static byte[] Header(int frameType) => [(byte)((frameType << 3) | 0x04)];

  private static byte[] Frame(int frameType) {
    var size = 1 + AmrNbCodec.PayloadBytes(frameType);
    var f = new byte[size];
    f[0] = (byte)((frameType << 3) | 0x04);
    return f;
  }

  [Test]
  public void ReadInfo_WalksMixedFrames() {
    // one 12.2 speech frame, one SID, one NO_DATA
    var stream = Concat(Frame(7), Frame(8), Header(15));
    var infos = AmrNbCodec.ReadInfo(stream);
    Assert.That(infos.Count, Is.EqualTo(3));
    Assert.That(infos[0].Mode, Is.EqualTo(AmrNbMode.Mr122));
    Assert.That(infos[0].SizeBytes, Is.EqualTo(32));
    Assert.That(infos[1].Mode, Is.EqualTo(AmrNbMode.MrdtxSid));
    Assert.That(infos[1].SizeBytes, Is.EqualTo(6));
    Assert.That(infos[2].Mode, Is.EqualTo(AmrNbMode.NoData));
    Assert.That(infos[2].SizeBytes, Is.EqualTo(1));
  }

  [Test]
  public void ReadInfo_IgnoresTruncatedTrailingFrame() {
    // a 12.2 header byte but only 5 of 31 payload bytes present
    var stream = new byte[6];
    stream[0] = (byte)((7 << 3) | 0x04);
    Assert.That(AmrNbCodec.CountFrames(stream), Is.EqualTo(0));
  }

  [Test]
  public void Decode_ProducesExactlyOneHundredSixtySamplesPerFrame() {
    var stream = Concat(Frame(7), Frame(0), Frame(8), Header(15));
    var pcm = AmrNbCodec.Decode(stream);
    Assert.That(pcm.Length, Is.EqualTo(4 * AmrNbCodec.SamplesPerFrame));
  }

  [Test]
  public void Decode_SidAndNoData_AreSilence() {
    var stream = Concat(Frame(8), Header(15));
    var pcm = AmrNbCodec.Decode(stream);
    Assert.That(pcm.Length, Is.EqualTo(2 * 160));
    Assert.That(pcm, Is.All.EqualTo((short)0));
  }

  [Test]
  public void Decode_IsDeterministic() {
    var stream = Concat(Frame(7), Frame(4));
    Assert.That(AmrNbCodec.Decode(stream), Is.EqualTo(AmrNbCodec.Decode(stream)));
  }

  [Test]
  public void Decode_GoldenFirstFrame_MatchesFfmpegReference() {
    // One 12.2 kbit/s frame (payload after the mode byte) produced by ffmpeg's libopencore_amrnb;
    // sampled golden values are this codec's float decode versus ffmpeg's AMR-NB float decoder
    // (PSNR > 100 dB / essentially identical on the first frame).
    byte[] frame0 = {
      60, 36, 2, 7, 72, 16, 75, 199, 232, 204, 1, 250, 247, 80, 69, 21,
      192, 0, 97, 28, 13, 109, 131, 32, 0, 0, 26, 225, 32, 6, 235, 176,
    };
    (int Index, short Value)[] golden = {
      (0, 0), (10, -1), (20, 0), (30, -3), (40, 54), (50, -768), (60, 1141), (70, -1485),
      (80, 1802), (90, -2737), (100, 2930), (110, -2087), (120, 802), (130, 467),
      (140, -1731), (150, 2677),
    };

    var pcm = AmrNbCodec.Decode(frame0);
    Assert.That(pcm.Length, Is.EqualTo(160));
    foreach (var (index, value) in golden)
      Assert.That(pcm[index], Is.EqualTo(value).Within(2), $"sample {index}");
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
