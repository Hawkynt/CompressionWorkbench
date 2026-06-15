using Codec.RoqDpcm;

namespace Compression.Tests.Codecs.RoqDpcm;

[TestFixture]
public class RoqDpcmCodecTests {

  // ──────────── Square-table decode (hand-computed) ────────────

  // table[b] = b<128 ? b*b : -((b-128)^2). predictor starts at arg.
  [Test]
  public void DecodeMono_SquareTable_AccumulatesDeltas() {
    // arg 0; bytes [3, 2, 130] → deltas 9, 4, -(2^2)=-4.
    //   0+9=9, 9+4=13, 13-4=9.
    var output = RoqDpcmCodec.Decode([3, 2, 130], initialArg: 0, stereo: false);
    Assert.That(output, Is.EqualTo(new short[] { 9, 13, 9 }));
  }

  [Test]
  public void DecodeMono_InitialPredictorFromArg() {
    // arg 1000; first byte 0 → delta 0 → stays 1000.
    var output = RoqDpcmCodec.Decode([0, 10], initialArg: 1000, stereo: false);
    Assert.That(output[0], Is.EqualTo(1000));
    Assert.That(output[1], Is.EqualTo(1000 + 100)); // 10^2
  }

  [Test]
  public void DecodeStereo_SplitsArgIntoLeftHighRightLow() {
    // arg 0x0102 → left = 0x0100 = 256, right = 0x02<<8 = 512.
    // payload L,R,L,R: [1,1, 2,2] → L: 256+1=257, +4=261; R: 512+1=513, +4=517.
    var output = RoqDpcmCodec.Decode([1, 1, 2, 2], initialArg: 0x0102, stereo: true);
    Assert.That(output[0], Is.EqualTo(257)); // L
    Assert.That(output[1], Is.EqualTo(513)); // R
    Assert.That(output[2], Is.EqualTo(261)); // L
    Assert.That(output[3], Is.EqualTo(517)); // R
  }

  // ──────────── Round-trip ────────────

  [Test]
  public void EncodeThenDecode_Mono_TracksWaveform() {
    var pcm = new short[300];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(i / 11.0) * 6000);

    var (payload, arg) = RoqDpcmCodec.Encode(pcm, stereo: false);
    var decoded = RoqDpcmCodec.Decode(payload, arg, stereo: false);

    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
    double err = 0;
    for (var i = 0; i < pcm.Length; ++i)
      err += Math.Abs(decoded[i] - pcm[i]);
    err /= pcm.Length;
    Assert.That(err, Is.LessThan(300.0));
  }

  [Test]
  public void EncodeThenDecode_Stereo_ProducesInterleavedOutput() {
    var pcm = new short[200];
    for (var i = 0; i < pcm.Length; i += 2) {
      pcm[i] = (short)(Math.Sin(i / 13.0) * 4000);      // L
      pcm[i + 1] = (short)(Math.Cos(i / 13.0) * 4000);  // R
    }

    var (payload, arg) = RoqDpcmCodec.Encode(pcm, stereo: true);
    var decoded = RoqDpcmCodec.Decode(payload, arg, stereo: true);
    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
  }
}
