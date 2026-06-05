#pragma warning disable CS1591
using Codec.WmaLossless;

namespace Compression.Tests.Codecs.Wma;

/// <summary>
/// Pins the Windows Media Audio Lossless decoder (WAVEFORMATEX tag <c>0x0163</c>): the
/// extradata-driven construction (bit depth, channel mask, decode flags, frame length),
/// the load-bearing integer kernels that make reconstruction bit-exact (the
/// scalar-product-and-MADD CDLMS step, hand-walked against the FFmpeg reference), and the
/// graceful behaviour on truncated / unsupported input. Real WMA Lossless streams come
/// from the Microsoft encoder; the integer pipelines are therefore verified directly with
/// exact small inputs rather than against a captured stream.
/// </summary>
[TestFixture]
public class WmaLosslessCodecTests {

  // WAVEFORMATEX extradata tail: bits @0, channel mask @2, decode flags @14.
  private static byte[] Extradata(int bits, uint channelMask, int decodeFlags) {
    var e = new byte[18];
    e[0] = (byte)(bits & 0xFF);
    e[1] = (byte)(bits >> 8);
    e[2] = (byte)(channelMask & 0xFF);
    e[3] = (byte)((channelMask >> 8) & 0xFF);
    e[4] = (byte)((channelMask >> 16) & 0xFF);
    e[5] = (byte)((channelMask >> 24) & 0xFF);
    e[14] = (byte)(decodeFlags & 0xFF);
    e[15] = (byte)((decodeFlags >> 8) & 0xFF);
    return e;
  }

  // ── construction / extradata parse ────────────────────────────────────────

  [Test]
  public void Ctor_ParsesBitDepthAndDecodeFlags() {
    var codec = new WmaLosslessCodec(2, 44100, 2048, Extradata(16, 0x3, decodeFlags: 0x40));
    Assert.That(codec.BitsPerSample, Is.EqualTo(16));
    Assert.That(codec.DecodeFlags, Is.EqualTo(0x40u));
    Assert.That(codec.UsesLengthPrefix, Is.True);
    Assert.That(codec.Channels, Is.EqualTo(2)); // popcount(0x3) == 2
  }

  [Test]
  public void Ctor_ChannelCountFromMaskOverridesArgument() {
    // Channel mask 0x33 (FL,FR,BL,BR) → 4 channels, regardless of the nChannels argument.
    var codec = new WmaLosslessCodec(2, 44100, 2048, Extradata(16, 0x33, 0x40));
    Assert.That(codec.Channels, Is.EqualTo(4));
  }

  [Test]
  public void Ctor_24BitIsAccepted() {
    var codec = new WmaLosslessCodec(1, 48000, 4096, Extradata(24, 0, 0x40));
    Assert.That(codec.BitsPerSample, Is.EqualTo(24));
  }

  [Test]
  public void Ctor_RejectsUnknownBitDepth() {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      new WmaLosslessCodec(1, 44100, 2048, Extradata(8, 0, 0x40)));
  }

  [Test]
  public void Ctor_RejectsTooShortExtradata() {
    Assert.Throws<ArgumentException>(() =>
      new WmaLosslessCodec(1, 44100, 2048, new byte[10]));
  }

  [Test]
  public void Ctor_RejectsInvalidBlockAlign() {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      new WmaLosslessCodec(1, 44100, 0, Extradata(16, 0, 0x40)));
  }

  [Test]
  public void Ctor_FrameLengthFollowsSampleRate() {
    // sampleRate <= 16000 → 9-bit frame → 512 samples (no decode-flag length tweak).
    var low = new WmaLosslessCodec(1, 16000, 2048, Extradata(16, 0, 0x40));
    Assert.That(low.SamplesPerFrame, Is.EqualTo(512));
    // 22050 < sr <= 48000 → 11-bit → 2048 samples.
    var mid = new WmaLosslessCodec(1, 44100, 2048, Extradata(16, 0, 0x40));
    Assert.That(mid.SamplesPerFrame, Is.EqualTo(2048));
  }

  // ── CDLMS scalar-product-and-MADD kernel (bit-exact integer step) ─────────

  [Test]
  public void ScalarProductAndMadd_ComputesDotProductAndAdaptsCoefs() {
    // res = sum(coefs[i] * prev[i]); each coefs[i] += mul * updates[i] AFTER its term is
    // added to res. With coefs={2,3}, prev={5,7}, updates={1,-1}, mul=2:
    //   res = 2*5 + (2 + 2*1=4)*... no — the reference reads coefs[i] for the product
    //   BEFORE updating coefs[i]; the running coefs change does not feed back into res
    //   for the same i. So res = 2*5 + 3*7 = 10 + 21 = 31.
    //   coefs become {2 + 2*1, 3 + 2*(-1)} = {4, 1}.
    var coefs = new short[] { 2, 3 };
    var prev = new[] { 5, 7 };
    var updates = new short[] { 1, -1 };
    var res = WmaLosslessCodec.TestScalarProductAndMadd(coefs, prev, updates, order: 2, mul: 2);
    Assert.That(res, Is.EqualTo(31));
    Assert.That(coefs, Is.EqualTo(new short[] { 4, 1 }));
  }

  [Test]
  public void ScalarProductAndMadd_ZeroMulLeavesCoefsUnchanged() {
    var coefs = new short[] { -4, 6, 8, -2 };
    var prev = new[] { 1, 2, 3, 4 };
    var updates = new short[] { 9, 9, 9, 9 };
    var res = WmaLosslessCodec.TestScalarProductAndMadd(coefs, prev, updates, order: 4, mul: 0);
    Assert.That(res, Is.EqualTo(-4 * 1 + 6 * 2 + 8 * 3 + -2 * 4)); // 24
    Assert.That(coefs, Is.EqualTo(new short[] { -4, 6, 8, -2 }));
  }

  [Test]
  public void ScalarProductAndMadd_NegativeMulSubtracts() {
    var coefs = new short[] { 10, 10 };
    var prev = new[] { 1, 1 };
    var updates = new short[] { 5, 3 };
    WmaLosslessCodec.TestScalarProductAndMadd(coefs, prev, updates, order: 2, mul: -1);
    Assert.That(coefs, Is.EqualTo(new short[] { 5, 7 })); // 10-5, 10-3
  }

  // ── end-to-end graceful behaviour ─────────────────────────────────────────

  [Test]
  public void DecodePacket_ShortPacket_IsTreatedAsLossNotThrow() {
    var codec = new WmaLosslessCodec(2, 44100, 2048, Extradata(16, 0x3, 0x40));
    short[] outp = null!;
    Assert.DoesNotThrow(() => outp = codec.DecodePacket(new byte[10]));
    Assert.That(outp, Is.Empty);
  }

  [Test]
  public void DecodePacket_FirstFrameIsSkipped_NoOutput() {
    // The reference skips the very first frame; an all-zero packet feeds the reservoir and
    // (at most) the skipped first frame, so the first packet produces no PCM.
    var codec = new WmaLosslessCodec(1, 16000, 2048, Extradata(16, 0, 0x40));
    var outp = codec.DecodePacket(new byte[2048]);
    Assert.That(outp, Is.Empty);
  }

  [Test]
  public void DecodePacket_ArbitraryBytes_DoesNotThrow() {
    var codec = new WmaLosslessCodec(2, 44100, 2048, Extradata(16, 0x3, 0x40));
    var rnd = new Random(1234);
    var pkt = new byte[2048];
    rnd.NextBytes(pkt);
    Assert.DoesNotThrow(() => codec.DecodePacket(pkt));
  }
}
