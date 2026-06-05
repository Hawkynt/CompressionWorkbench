#pragma warning disable CS1591
using Thp = Codec.AdpcmX.Thp;
using EaR = Codec.AdpcmX.EaR;
using DspAdpcmCodec = Codec.DspAdpcm.DspAdpcmCodec;

namespace Compression.Tests.Codecs.AdpcmX;

[TestFixture]
public class ThpAfcEaRTests {

  // THP frame, header 0x00 ⇒ predictor index 0, exponent 0. With a zero coef table the predictor
  // contribution is 0, so each sample is the bare sign-extended nibble (HIGH nibble first).
  [Test]
  public void Thp_TrivialCoefs_YieldsBareNibbles() {
    var coefs = new short[16];
    var frame = new byte[] {
      0x00,
      0x12, 0x34, 0x56, 0x70, // 1,2,3,4,5,6,7,0
      0xFE, 0xDC, 0xBA,       // -1,-2,-3,-4,-5,-6
    };
    var pcm = Thp.DecodeThp(frame, coefs, 14);
    Assert.That(pcm, Is.EqualTo(new short[] { 1, 2, 3, 4, 5, 6, 7, 0, -1, -2, -3, -4, -5, -6 }));
  }

  // THP exponent scales the nibble: exp=1 ⇒ each nibble ×2.
  [Test]
  public void Thp_ExponentScalesNibble() {
    var coefs = new short[16];
    var frame = new byte[] { 0x01, 0x12, 0, 0, 0, 0, 0, 0 };
    var pcm = Thp.DecodeThp(frame, coefs, 2);
    Assert.That(pcm, Is.EqualTo(new short[] { 2, 4 }));
  }

  // THP with a non-trivial coef pair must match Codec.DspAdpcm only where the formulas coincide;
  // instead pin the THP formula directly: coefs (2048, 0) ⇒ factor1=2048 (Q11 → ×1.0). header
  // index 1 exp 0. First nibble 4 (HIGH): pred contribution (2048*0)>>11=0, +4 ⇒ 4. Next nibble 0:
  // (2048*4)>>11 = 4, +0 ⇒ 4. Next nibble 0: (2048*4)>>11=4 ⇒ 4.
  [Test]
  public void Thp_AppliesPredictorContribution() {
    var coefs = new short[16];
    coefs[2] = 2048; // index 1, factor1
    var frame = new byte[] { 0x10, 0x40, 0x00, 0, 0, 0, 0, 0 }; // index 1, exp 0; nibbles 4,0,0,0
    var pcm = Thp.DecodeThp(frame, coefs, 3);
    Assert.That(pcm, Is.EqualTo(new short[] { 4, 4, 4 }));
  }

  // AFC frame is 9 bytes (16 samples). header 0x00 ⇒ exp 0 (high), index 0 (low) ⇒ coefs (0,0).
  // Bare sign-extended nibbles, HIGH first.
  [Test]
  public void Afc_TrivialIndex_YieldsBareNibbles() {
    var frame = new byte[] {
      0x00,
      0x12, 0x34, 0x56, 0x70, 0xFE, 0xDC, 0xBA, 0x98, // 16 nibbles
    };
    var pcm = Thp.DecodeAfc(frame, 16);
    Assert.That(pcm, Is.EqualTo(new short[] { 1, 2, 3, 4, 5, 6, 7, 0, -1, -2, -3, -4, -5, -6, -7, -8 }));
  }

  // AFC exponent is the HIGH nibble: header 0x10 ⇒ exp 1, index 0. nibble 4 ⇒ 4<<1 = 8.
  [Test]
  public void Afc_HighNibbleIsExponent() {
    var frame = new byte[] { 0x10, 0x40, 0, 0, 0, 0, 0, 0, 0 };
    var pcm = Thp.DecodeAfc(frame, 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 8 }));
  }

  // AFC index 1 selects AfcCoefs (2048, 0) i.e. factor1=2048 (×1.0). header 0x01 ⇒ exp 0 index 1.
  // nibble 4 ⇒ 0 + 4 = 4; nibble 0 ⇒ (2048*4)>>11 = 4.
  [Test]
  public void Afc_IndexSelectsCoefPair() {
    var frame = new byte[] { 0x01, 0x40, 0, 0, 0, 0, 0, 0, 0 };
    var pcm = Thp.DecodeAfc(frame, 2);
    Assert.That(pcm, Is.EqualTo(new short[] { 4, 4 }));
  }

  // Cross-check: an AFC index-0 / exp-0 stream and a DSP predictor-0 / shift-0 stream both reduce
  // to bare nibbles, so they agree on that path (the broader formulas differ by the DSP +1024
  // rounding and whole-expression shift, which is why they are separate codecs).
  [Test]
  public void Afc_AndDsp_AgreeOnBareNibblePath() {
    var dspFrame = new byte[] { 0x00, 0x12, 0x34, 0x56, 0x70, 0x00, 0x00, 0x00 };
    var dsp = DspAdpcmCodec.Decode(dspFrame, new short[16], 8);
    var afcFrame = new byte[] { 0x00, 0x12, 0x34, 0x70, 0x00, 0, 0, 0, 0 };
    var afc = Thp.DecodeAfc(afcFrame, 8);
    Assert.That(afc[..6], Is.EqualTo(new short[] { 1, 2, 3, 4, 7, 0 }));
    Assert.That(dsp[..6], Is.EqualTo(new short[] { 1, 2, 3, 4, 5, 6 }));
  }

  [Test]
  public void Thp_TruncatesFinalFrame() {
    var coefs = new short[16];
    var frame = new byte[] { 0x00, 0x12, 0x34, 0x56, 0x70, 0, 0, 0 };
    var pcm = Thp.DecodeThp(frame, coefs, 5);
    Assert.That(pcm.Length, Is.EqualTo(5));
    Assert.That(pcm, Is.EqualTo(new short[] { 1, 2, 3, 4, 5 }));
  }

  // EA R1 mono: start samples (current, previous) LE16 then one frame. coefs index 0 ⇒ K0=K1=0.
  // header high nibble 0 (coef 0), low nibble 0 ⇒ shift = 20 - 0 = 20. A nibble n contributes
  // (n << 20 + 0) >> 8. nibble 1 ⇒ (1<<20)>>8 = 4096 (clamped to 16-bit ⇒ 4096). nibble 2 ⇒ 8192.
  [Test]
  public void EaR1_TrivialCoef_ShiftedNibbles() {
    var data = new byte[] {
      0x00, 0x00, // current sample 0
      0x00, 0x00, // previous sample 0
      0x00,       // header: coef 0, shift 20
      0x12,       // nibbles 1,2
      0x00,       // 0,0
    };
    var pcm = EaR.DecodeChannel(data, EaR.Revision.R1, sampleCount: 2);
    Assert.That(pcm, Is.EqualTo(new short[] { 4096, 8192 }));
  }

  // EA R1 raw escape: header 0xEE introduces raw BE16 samples.
  [Test]
  public void EaR1_RawEscape_CopiesSamples() {
    var data = new byte[] {
      0x00, 0x00, 0x00, 0x00,             // seed samples
      0xEE,                               // raw marker
      0x12, 0x34, 0xFF, 0xFF,             // raw BE16: 0x1234, -1
    };
    var pcm = EaR.DecodeChannel(data, EaR.Revision.R1, sampleCount: 2);
    Assert.That(pcm, Is.EqualTo(new short[] { 0x1234, -1 }));
  }

  // EA R2/R3 carry no in-band seed (the predictor persists in decoder state); the caller supplies
  // it. R3 has big-endian framing but the raw escape is big-endian in every revision.
  [Test]
  public void EaR3_NoInBandSeed_RawEscapeBigEndian() {
    var data = new byte[] {
      0xEE,
      0x00, 0x05, // one raw BE16 sample = 5
    };
    var pcm = EaR.DecodeChannel(data, EaR.Revision.R3, sampleCount: 1, seedHist1: 123, seedHist2: 0);
    Assert.That(pcm[0], Is.EqualTo(5));
  }
}
