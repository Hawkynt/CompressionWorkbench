#pragma warning disable CS1591
using Codec.DspAdpcm;

namespace Compression.Tests.Codecs.DspAdpcm;

[TestFixture]
public class DspAdpcmCodecTests {

  // With predictor 0 and coefs[0]=coefs[1]=0 and shift=0 the decode collapses to the bare
  // sign-extended nibble: sample = ((nib<<0)<<11 + 0 + 0 + 1024) >> 11 = nib.
  [Test]
  public void Decode_TrivialCoefs_YieldsBareNibbles() {
    var coefs = new short[16]; // all zero ⇒ predictor 0 pair is (0,0)
    // header: predictor 0 (high nibble) + shift 0 (low nibble) = 0x00
    // 14 nibbles, HIGH nibble first: 1,2,3,4,5,6,7,0, then -1(0xF),-2(0xE),-3(0xD),-4(0xC),-5(0xB),-6(0xA)
    var frame = new byte[] {
      0x00,
      0x12, 0x34, 0x56, 0x70, // 1,2,3,4,5,6,7,0
      0xFE, 0xDC, 0xBA, 0x00, // -1,-2,-3,-4,-5,-6,0,0
    };
    var samples = DspAdpcmCodec.Decode(frame, coefs, 14);

    Assert.That(samples, Is.EqualTo(new short[] { 1, 2, 3, 4, 5, 6, 7, 0, -1, -2, -3, -4, -5, -6 }));
  }

  // A non-zero shift scales the nibble: shift=1 ⇒ each nibble multiplied by 2.
  [Test]
  public void Decode_ShiftScalesNibble() {
    var coefs = new short[16];
    var frame = new byte[] {
      0x01,             // predictor 0, shift 1
      0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };
    var samples = DspAdpcmCodec.Decode(frame, coefs, 2);
    Assert.That(samples, Is.EqualTo(new short[] { 2, 4 }));
  }

  [Test]
  public void Decode_TruncatesFinalFrame() {
    var coefs = new short[16];
    var frame = new byte[] {
      0x00,
      0x12, 0x34, 0x56, 0x70, 0x00, 0x00, 0x00,
    };
    // Only ask for 5 of the 14 samples in the frame.
    var samples = DspAdpcmCodec.Decode(frame, coefs, 5);
    Assert.That(samples.Length, Is.EqualTo(5));
    Assert.That(samples, Is.EqualTo(new short[] { 1, 2, 3, 4, 5 }));
  }

  [Test]
  public void EncodeDecode_SineRoundTripsWithinTolerance() {
    const int n = 4096;
    var pcm = new short[n];
    for (var i = 0; i < n; ++i)
      pcm[i] = (short)(Math.Sin(i * 2.0 * Math.PI / 64.0) * 12000.0);

    var (adpcm, coefs) = DspAdpcmCodec.Encode(pcm);
    var decoded = DspAdpcmCodec.Decode(adpcm, coefs, n);

    Assert.That(decoded.Length, Is.EqualTo(n));

    double sumSq = 0;
    for (var i = 0; i < n; ++i) {
      double d = decoded[i] - pcm[i];
      sumSq += d * d;
    }
    var rms = Math.Sqrt(sumSq / n);
    // 4-bit ADPCM on a clean tone should track the signal closely.
    Assert.That(rms, Is.LessThan(800.0), $"RMS error {rms} too high");
  }

  [Test]
  public void Encode_ProducesEightBytePerFrameMultiple() {
    var pcm = new short[14 * 3 + 5]; // 4 frames worth (last partial)
    for (var i = 0; i < pcm.Length; ++i) pcm[i] = (short)(i * 37);
    var (adpcm, _) = DspAdpcmCodec.Encode(pcm);
    Assert.That(adpcm.Length % DspAdpcmCodec.BytesPerFrame, Is.EqualTo(0));
    Assert.That(adpcm.Length, Is.EqualTo(4 * DspAdpcmCodec.BytesPerFrame));
  }

  [Test]
  public void Decode_RejectsShortCoefs()
    => Assert.Throws<ArgumentException>(() => DspAdpcmCodec.Decode(new byte[8], new short[15], 1));
}
