#pragma warning disable CS1591
using ImaDk3 = Codec.AdpcmX.ImaDk3;
using ImaDk4 = Codec.AdpcmX.ImaDk4;
using ImaEa = Codec.AdpcmX.ImaEa;
using FourXm = Codec.AdpcmX.FourXm;

namespace Compression.Tests.Codecs.AdpcmX;

[TestFixture]
public class ImaVariantsTests {

  // DK4 mono: header predictor=100 index=2, then one data byte 0x35.
  // Start predictor 100 is emitted first. Then high nibble 3 (step[2]=9 ⇒ +7 ⇒107, idx→1),
  // low nibble 5 (step[1]=8 ⇒ +11 ⇒118, idx→5). Hand-walked against adpcm_ima_expand_nibble.
  [Test]
  public void Dk4_Mono_FirstSamples() {
    var block = new byte[] { 0x64, 0x00, 0x02, 0x00, 0x35 };
    var pcm = ImaDk4.Decode(block, channels: 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 100, 107, 118 }));
  }

  // DK4 stereo: two 4-byte headers (L pred 100 idx 2, R pred -50 idx 0), one data byte 0x35.
  // High nibble → L, low nibble → R. L start 100, R start -50 emitted first (interleaved).
  // High 3: L step[2]=9 ⇒ +7 ⇒107. Low 5: R step[0]=7 ⇒ diff 7>>3=0 +7(bit4) +7>>2=1(bit0) =8 ⇒ -50+8=-42.
  [Test]
  public void Dk4_Stereo_FirstSamples() {
    var block = new byte[] {
      0x64, 0x00, 0x02, 0x00,   // L predictor 100, index 2
      0xCE, 0xFF, 0x00, 0x00,   // R predictor -50 (0xFFCE), index 0
      0x35,
    };
    var pcm = ImaDk4.Decode(block, channels: 2);
    // interleaved L,R: starts (100,-50) then (107,-42)
    Assert.That(pcm, Is.EqualTo(new short[] { 100, -50, 107, -42 }));
  }

  [Test]
  public void Dk4_RejectsBadChannels()
    => Assert.Throws<ArgumentException>(() => ImaDk4.Decode(new byte[8], channels: 3));

  // DK3 is always stereo. Header: 10 skip, sum pred 0, diff pred 0, sum idx 0, diff idx 0.
  // Payload nibbles low-first: byte 0x04 ⇒ low=4 (sum), high=0 (diff); byte 0x00 ⇒ low=0 (sum).
  // Nibble order consumed: sum=4, diff=0, sum=0.
  //  sum n=4: step[0]=7, diff 7>>3=0 +7(bit4)=7 ⇒ sum 7, sumIdx→2.
  //  diff n=0: step[0]=7, diff 7>>3=0 ⇒ diff 0.  Pair (sum+diff, sum-diff) = (7,7).
  //  sum n=0: step[2]=9, diff 9>>3=1 ⇒ sum 8.    Pair (8,8).
  [Test]
  public void Dk3_DecodesCoupledSumDiff() {
    var block = new byte[16 + 2];
    block[14] = 0; block[15] = 0; // indices
    block[16] = 0x04; // low nibble 4 → sum, high nibble 0 → diff
    block[17] = 0x00; // low nibble 0 → sum
    var pcm = ImaDk3.Decode(block);
    Assert.That(pcm.Length, Is.EqualTo(4));
    Assert.That(pcm, Is.EqualTo(new short[] { 7, 7, 8, 8 }));
  }

  // EACS mono: step index (LE32) = 0, predictor (LE32) = 1000. One byte 0x40.
  // HIGH nibble 4 first (step[0]=7 ⇒ +7 ⇒1007, idx→2), LOW nibble 0 (step[2]=9 ⇒ +1 ⇒1008).
  [Test]
  public void Eacs_Mono_FirstSamples() {
    var data = new byte[] {
      0x00, 0x00, 0x00, 0x00,   // step index 0
      0xE8, 0x03, 0x00, 0x00,   // predictor 1000
      0x40,
    };
    var pcm = ImaEa.DecodeEacs(data, channels: 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 1007, 1008 }));
  }

  // SEAD shift is 6. Start predictor 0 index 0. Byte 0x40: HIGH 4 (step[0]=7, diff 7>>6=0,
  // bit4 adds the full step 7 ⇒ +7 ⇒7, idx→2), LOW 0 (step[2]=9 ⇒ diff 9>>6=0 ⇒ +0 ⇒7).
  [Test]
  public void Sead_Shift6_FirstSamples() {
    var pcm = ImaEa.DecodeSead(new byte[] { 0x40 }, channels: 1,
      startPredictors: [0], startIndices: [0]);
    Assert.That(pcm, Is.EqualTo(new short[] { 7, 7 }));
  }

  // 4XM mono shift 4. Header pred=200 idx=3, payload byte 0x12 (LOW nibble first).
  // step[3]=10. Low nibble 2: shift4 diff 10>>4=0, bit1(2)→10>>1=5 ⇒ +5 ⇒205, idx+=adj[2]=-1→2.
  // High nibble 1: step[2]=9, diff 9>>4=0, bit0(1)→9>>2=2 ⇒ +2 ⇒207.
  [Test]
  public void FourXm_Mono_FirstSamples() {
    var block = new byte[] { 0xC8, 0x00, 0x03, 0x00, 0x12 };
    var pcm = FourXm.Decode(block, channels: 1);
    Assert.That(pcm, Is.EqualTo(new short[] { 205, 207 }));
  }

  [Test]
  public void FourXm_LengthArithmetic() {
    // header 4 bytes (mono) + 10 payload bytes ⇒ 20 samples.
    var block = new byte[14];
    block[0] = 0; block[1] = 0; block[2] = 0; block[3] = 0;
    var pcm = FourXm.Decode(block, channels: 1);
    Assert.That(pcm.Length, Is.EqualTo(20));
  }
}
