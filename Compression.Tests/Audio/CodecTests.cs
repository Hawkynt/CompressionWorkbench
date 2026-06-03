#pragma warning disable CS1591
using Codec.ALaw;
using Codec.Gsm610;
using Codec.ImaAdpcm;
using Codec.MuLaw;

namespace Compression.Tests.Audio;

[TestFixture]
public class CodecTests {

  // ── μ-law ───────────────────────────────────────────────────────────────

  [Test]
  public void MuLaw_KnownDecodeValues() {
    // μ-law 0xFF (+0) should decode to 0; 0x7F (-0 after bias flip) also ~0.
    Assert.That(MuLawCodec.DecodeSample(0xFF), Is.EqualTo(0));
    // 0x80 is the most negative; 0x00 is the most positive (before bit-inversion).
    Assert.That(MuLawCodec.DecodeSample(0x00), Is.LessThan(0));
    Assert.That(MuLawCodec.DecodeSample(0x80), Is.GreaterThan(0));
  }

  [Test]
  public void MuLaw_RoundTrip_PreservesSign() {
    // μ-law is lossy but monotonic; encoded-then-decoded samples should keep sign.
    short[] inputs = [-32000, -8000, -100, 0, 100, 8000, 32000];
    foreach (var x in inputs) {
      var enc = MuLawCodec.EncodeSample(x);
      var dec = MuLawCodec.DecodeSample(enc);
      if (x == 0) {
        Assert.That(Math.Abs(dec), Is.LessThan(16), $"zero should decode near zero, got {dec}");
      } else {
        Assert.That(Math.Sign(dec), Is.EqualTo(Math.Sign(x)),
          $"sign mismatch for input {x} → enc 0x{enc:X2} → dec {dec}");
      }
    }
  }

  [Test]
  public void MuLaw_Decode_ProducesOneShortPerByte() {
    var input = new byte[256];
    for (var i = 0; i < 256; ++i) input[i] = (byte)i;
    var pcm = MuLawCodec.Decode(input);
    Assert.That(pcm.Length, Is.EqualTo(256));
  }

  // ── A-law ───────────────────────────────────────────────────────────────

  [Test]
  public void ALaw_KnownDecodeValues() {
    // A-law 0xD5 = 0x55 ^ 0x80 with exp=0, mantissa=0 → 8 in magnitude, sign=1 → 8.
    var zero = ALawCodec.DecodeSample(0xD5);
    Assert.That(Math.Abs(zero), Is.LessThan(32));
  }

  [Test]
  public void ALaw_RoundTrip_PreservesSign() {
    short[] inputs = [-16000, -200, 0, 200, 16000];
    foreach (var x in inputs) {
      var enc = ALawCodec.EncodeSample(x);
      var dec = ALawCodec.DecodeSample(enc);
      if (x == 0) Assert.That(Math.Abs(dec), Is.LessThan(32));
      else Assert.That(Math.Sign(dec), Is.EqualTo(Math.Sign(x)));
    }
  }

  [Test]
  public void ALaw_Decode_ProducesOneShortPerByte() {
    var input = new byte[128];
    for (var i = 0; i < 128; ++i) input[i] = (byte)(i * 2);
    var pcm = ALawCodec.Decode(input);
    Assert.That(pcm.Length, Is.EqualTo(128));
  }

  // ── IMA ADPCM ───────────────────────────────────────────────────────────

  [Test]
  public void ImaAdpcm_DecodesOneMonoBlock() {
    // Block layout (blockAlign=256, mono): 2-byte predictor + 1-byte idx + 1-byte pad +
    // 252 bytes of nibble pairs. 252*2 + 1 = 505 samples.
    var block = new byte[256];
    // Predictor = 0, index = 0, then 252 bytes of 0x00 (which decodes to tiny deltas).
    var perChannel = ImaAdpcmCodec.Decode(block, blockAlign: 256, channels: 1);
    Assert.That(perChannel.Length, Is.EqualTo(1));
    Assert.That(perChannel[0].Length, Is.EqualTo(505));
    // First sample equals the block's predictor (0).
    Assert.That(perChannel[0][0], Is.EqualTo(0));
  }

  [Test]
  public void ImaAdpcm_DecodesMultipleMonoBlocks() {
    var blocks = new byte[256 * 3];
    var perChannel = ImaAdpcmCodec.Decode(blocks, blockAlign: 256, channels: 1);
    Assert.That(perChannel.Length, Is.EqualTo(1));
    Assert.That(perChannel[0].Length, Is.EqualTo(505 * 3));
  }

  [Test]
  public void ImaAdpcm_DecodesStereoBlock() {
    // Stereo blockAlign=512 → 8 bytes header + 504 bytes data → 504*2/2 + 1 = 505 samples/channel.
    var block = new byte[512];
    var perChannel = ImaAdpcmCodec.Decode(block, blockAlign: 512, channels: 2);
    Assert.That(perChannel.Length, Is.EqualTo(2));
    Assert.That(perChannel[0].Length, Is.EqualTo(505));
    Assert.That(perChannel[1].Length, Is.EqualTo(505));
  }

  // ── QuickTime IMA ADPCM ('ima4') ─────────────────────────────────────────

  // Builds one 34-byte QuickTime IMA packet: 2-byte BE preamble + 32 data bytes.
  private static byte[] QtPacket(short predictor, int stepIndex, byte dataByte) {
    var pkt = new byte[34];
    var preamble = (ushort)((predictor & 0xFF80) | (stepIndex & 0x7F));
    pkt[0] = (byte)(preamble >> 8);
    pkt[1] = (byte)(preamble & 0xFF);
    for (var i = 0; i < 32; ++i) pkt[2 + i] = dataByte;
    return pkt;
  }

  [Test]
  public void ImaAdpcmQuickTime_DecodesOneMonoPacket_KnownValues() {
    // Preamble 0x0000 → predictor 0, step index 0. Each data byte 0x21 → low nibble 1,
    // high nibble 2. Hand-walked first eight samples (low nibble first per byte).
    var packet = QtPacket(predictor: 0, stepIndex: 0, dataByte: 0x21);
    var perChannel = ImaAdpcmCodec.DecodeQuickTime(packet, channels: 1);

    Assert.That(perChannel.Length, Is.EqualTo(1));
    Assert.That(perChannel[0].Length, Is.EqualTo(64));
    Assert.That(perChannel[0][..8], Is.EqualTo(new short[] { 1, 4, 5, 8, 9, 12, 13, 16 }));
  }

  [Test]
  public void ImaAdpcmQuickTime_HonoursInitialPredictorAndIndex() {
    // Preamble 0x0102 → predictor 256, step index 2. Data byte 0x08 → low nibble 8
    // (sign-negative, magnitude 0), high nibble 0. First samples decay toward 256.
    var packet = QtPacket(predictor: 256, stepIndex: 2, dataByte: 0x08);
    var perChannel = ImaAdpcmCodec.DecodeQuickTime(packet, channels: 1);

    Assert.That(perChannel[0][..4], Is.EqualTo(new short[] { 255, 256, 256, 256 }));
  }

  [Test]
  public void ImaAdpcmQuickTime_RoundRobinsPacketsAcrossChannels() {
    // Two packets, two channels: packet 0 → ch0, packet 1 → ch1.
    var ch0 = QtPacket(predictor: 0, stepIndex: 0, dataByte: 0x21);
    var ch1 = QtPacket(predictor: 0, stepIndex: 0, dataByte: 0x08);
    var stream = new byte[68];
    ch0.CopyTo(stream, 0);
    ch1.CopyTo(stream, 34);

    var perChannel = ImaAdpcmCodec.DecodeQuickTime(stream, channels: 2);
    Assert.That(perChannel.Length, Is.EqualTo(2));
    Assert.That(perChannel[0].Length, Is.EqualTo(64));
    Assert.That(perChannel[1].Length, Is.EqualTo(64));
    Assert.That(perChannel[0][..4], Is.EqualTo(new short[] { 1, 4, 5, 8 }));     // ch0
    Assert.That(perChannel[1][..4], Is.EqualTo(new short[] { 0, 0, 0, 0 }));      // ch1 (nibble 8, mag 0)
  }

  // ── GSM 06.10 ──────────────────────────────────────────────────────────

  [Test]
  public void Gsm610_DecodesOneFrameToOneHundredSixtySamples() {
    var frame = new byte[Gsm610Codec.FrameBytes];
    var pcm = Gsm610Codec.Decode(frame, channels: 1);
    Assert.That(pcm.Length, Is.EqualTo(Gsm610Codec.FrameSamples));
  }

  [Test]
  public void Gsm610_DecodesMultipleFrames() {
    var frames = new byte[Gsm610Codec.FrameBytes * 4];
    var pcm = Gsm610Codec.Decode(frames, channels: 1);
    Assert.That(pcm.Length, Is.EqualTo(Gsm610Codec.FrameSamples * 4));
  }

  [Test]
  public void Gsm610_RejectsMisalignedInput() {
    var bad = new byte[Gsm610Codec.FrameBytes - 1];
    Assert.Throws<ArgumentException>(() => Gsm610Codec.Decode(bad, channels: 1));
  }
}
