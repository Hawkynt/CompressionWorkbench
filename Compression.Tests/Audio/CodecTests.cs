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
