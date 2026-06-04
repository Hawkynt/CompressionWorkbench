using Codec.WsAdpcm;

namespace Compression.Tests.Codecs.WsAdpcm;

[TestFixture]
public class StandardImaCodecTests {

  // ──────────── Known nibble walk (hand-computed) ────────────

  // predictor=0, index=0, step=7.
  //   byte 0x04 → low nibble 4: diff=7>>3 + 7 = 0+7 = 7 (bit4 set), predictor=7, index→2
  //              high nibble 0: step=StepTable[2]=9, diff=9>>3=1, predictor=8, index→1
  [Test]
  public void Decode_KnownNibbles_ProducesHandComputedSamples() {
    var state = new StandardImaCodec.State(0, 0);
    var output = StandardImaCodec.Decode([0x04], ref state);
    Assert.That(output.Length, Is.EqualTo(2));
    Assert.That(output[0], Is.EqualTo(7));  // low nibble first
    Assert.That(output[1], Is.EqualTo(8));
  }

  [Test]
  public void Decode_LowNibbleFirst() {
    // byte 0x40 → low nibble 0 (small step), high nibble 4 (big step). The first output
    // sample must come from the low nibble.
    var state = new StandardImaCodec.State(0, 0);
    var output = StandardImaCodec.Decode([0x40], ref state);
    // low nibble 0: diff=7>>3=0, predictor stays 0.
    Assert.That(output[0], Is.EqualTo(0));
    // high nibble 4: step still 7 (index after nibble0: 0-1 clamped to 0), diff=7, predictor=7.
    Assert.That(output[1], Is.EqualTo(7));
  }

  // ──────────── Round-trip closeness ────────────

  [Test]
  public void EncodeThenDecode_TracksWaveformWithinTolerance() {
    var pcm = new short[400];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(i / 9.0) * 8000);

    var encState = new StandardImaCodec.State(0, 0);
    var encoded = StandardImaCodec.Encode(pcm, ref encState);

    var decState = new StandardImaCodec.State(0, 0);
    var decoded = StandardImaCodec.Decode(encoded, ref decState);

    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
    double err = 0;
    for (var i = 0; i < pcm.Length; ++i)
      err += Math.Abs(decoded[i] - pcm[i]);
    err /= pcm.Length;
    // IMA tracks a slowly varying sine well below a few hundred LSB mean error.
    Assert.That(err, Is.LessThan(400.0));
  }

  [Test]
  public void Encode_OddSampleCount_PadsHighNibbleWithZero() {
    var state = new StandardImaCodec.State(0, 0);
    var encoded = StandardImaCodec.Encode([1000], ref state);
    Assert.That(encoded.Length, Is.EqualTo(1));
    Assert.That(encoded[0] >> 4, Is.EqualTo(0)); // high nibble padded
  }
}
