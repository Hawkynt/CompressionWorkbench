using Codec.XaAdpcm;

namespace Compression.Tests.Codecs.XaAdpcm;

[TestFixture]
public class XaAdpcmCodecTests {

  // Builds a 128-byte sound group with the given per-unit (filter, shift) params,
  // writing both the live (bytes 4..11) and the redundant copies the way a real
  // encoder would, so the parameter read is exercised at the canonical offset.
  private static byte[] NewGroup((int Filter, int Shift)[] units) {
    var group = new byte[XaAdpcmCodec.SoundGroupSize];
    for (var u = 0; u < units.Length; ++u) {
      var param = (byte)((units[u].Filter << 4) | units[u].Shift);
      if (u < 4) {
        group[u] = param;
        group[4 + u] = param;
      } else {
        group[4 + u] = param;
        group[8 + u] = param;
      }
    }
    return group;
  }

  // ──────────── 1. Hand-computed decode (filter 0 = pure shift) ────────────

  /// <summary>
  /// Filter 0 has K0 = K1 = 0, so the predictor vanishes and each sample is
  /// <c>signExtend4(nibble) &lt;&lt; 12 &gt;&gt; shift</c>. With shift = 12 the shifts cancel, so
  /// every decoded sample equals its sign-extended nibble. Unit 0 reads the low nibble
  /// of data column bytes (offset 16, 20, 24, …).
  /// </summary>
  [Test]
  public void Decode_Filter0_Shift12_NibblesMapDirectlyToSamples_Mono() {
    var units = new (int, int)[8];
    for (var u = 0; u < 8; ++u) units[u] = (0, 12);
    var group = NewGroup(units);

    // Unit 0's samples 0,1,2 ⇒ data bytes 16,20,24 low nibbles = 1, 2, F(-1).
    group[16] = 0x01;
    group[20] = 0x02;
    group[24] = 0x0F;

    var hist = new XaAdpcmCodec.History();
    var dummy = new XaAdpcmCodec.History();
    var output = new short[8 * 28];
    XaAdpcmCodec.DecodeGroup(group, stereo: false, ref hist, ref dummy, output);

    Assert.That(output[0], Is.EqualTo((short)1));
    Assert.That(output[1], Is.EqualTo((short)2));
    Assert.That(output[2], Is.EqualTo((short)(-1)));
    Assert.That(output[3], Is.EqualTo((short)0));
  }

  /// <summary>
  /// Filter 0, shift 0: <c>s = signExtend4(nibble) &lt;&lt; 12</c>. Nibble 1 → 4096,
  /// nibble F (-1) → -4096.
  /// </summary>
  [Test]
  public void Decode_Filter0_Shift0_LeftShiftsBy12() {
    var units = new (int, int)[8];
    var group = NewGroup(units); // all (0,0)
    group[16] = 0xF1; // unit 0 low nibble (sample 0) = 1; unit 1 high nibble (sample 0) = F

    var left = new XaAdpcmCodec.History();
    var dummy = new XaAdpcmCodec.History();
    var output = new short[8 * 28];
    XaAdpcmCodec.DecodeGroup(group, stereo: false, ref left, ref dummy, output);

    Assert.That(output[0], Is.EqualTo((short)4096));          // unit 0 sample 0
    Assert.That(output[28], Is.EqualTo((short)(-4096)));      // unit 1 sample 0 (mono → sequential)
  }

  // ──────────── 2. Nibble interleave across units ────────────

  /// <summary>
  /// The eight units of a group draw from the same 4-byte columns: unit u, sample i reads
  /// byte <c>16 + i*4 + (u&gt;&gt;1)</c> and the low nibble for even u, the high nibble for odd u.
  /// Crafting distinguishable first-sample nibbles per unit proves each unit reads exactly
  /// its slice. Mono output lays the units out sequentially, so sample 28*u is unit u's
  /// first sample.
  /// </summary>
  [Test]
  public void Decode_NibbleInterleave_EachUnitReadsItsOwnSlice() {
    var units = new (int, int)[8];
    for (var u = 0; u < 8; ++u) units[u] = (0, 12); // identity mapping
    var group = NewGroup(units);

    // Column byte for the FIRST sample of each unit pair is byte 16 + (u>>1):
    //   units 0/1 → byte 16, units 2/3 → byte 17, units 4/5 → byte 18, units 6/7 → byte 19.
    // Even unit = low nibble, odd unit = high nibble. Give each unit a unique value.
    group[16] = 0x21; // unit 0 → 1, unit 1 → 2
    group[17] = 0x43; // unit 2 → 3, unit 3 → 4
    group[18] = 0x65; // unit 4 → 5, unit 5 → 6
    group[19] = 0x76; // unit 6 → 6, unit 7 → 7

    var left = new XaAdpcmCodec.History();
    var dummy = new XaAdpcmCodec.History();
    var output = new short[8 * 28];
    XaAdpcmCodec.DecodeGroup(group, stereo: false, ref left, ref dummy, output);

    Assert.That(output[0 * 28], Is.EqualTo((short)1), "unit 0");
    Assert.That(output[1 * 28], Is.EqualTo((short)2), "unit 1");
    Assert.That(output[2 * 28], Is.EqualTo((short)3), "unit 2");
    Assert.That(output[3 * 28], Is.EqualTo((short)4), "unit 3");
    Assert.That(output[4 * 28], Is.EqualTo((short)5), "unit 4");
    Assert.That(output[5 * 28], Is.EqualTo((short)6), "unit 5");
    Assert.That(output[6 * 28], Is.EqualTo((short)6), "unit 6");
    Assert.That(output[7 * 28], Is.EqualTo((short)7), "unit 7");
  }

  // ──────────── 3. Stereo unit routing ────────────

  /// <summary>
  /// In stereo, even units feed LEFT and odd units feed RIGHT, and the decoder weaves them
  /// into L/R-interleaved output. Unit 0 (left) and unit 1 (right) both take their first
  /// sample from column byte 16 (low nibble for unit 0, high nibble for unit 1), so a single
  /// byte fixes both channels' first sample.
  /// </summary>
  [Test]
  public void Decode_Stereo_EvenUnitsLeft_OddUnitsRight() {
    var units = new (int, int)[8];
    for (var u = 0; u < 8; ++u) units[u] = (0, 12);
    var group = NewGroup(units);
    group[16] = 0x53; // unit 0 (left) → 3, unit 1 (right) → 5

    var left = new XaAdpcmCodec.History();
    var right = new XaAdpcmCodec.History();
    var output = new short[8 * 28];
    var written = XaAdpcmCodec.DecodeGroup(group, stereo: true, ref left, ref right, output);

    Assert.That(written, Is.EqualTo(8 * 28));
    Assert.That(output[0], Is.EqualTo((short)3), "left sample 0");
    Assert.That(output[1], Is.EqualTo((short)5), "right sample 0");
  }

  // ──────────── 4. Group/sample arithmetic ────────────

  [Test]
  public void Decode_TwoGroups_Yield448Samples() {
    var data = new byte[2 * XaAdpcmCodec.SoundGroupSize];
    var pcm = XaAdpcmCodec.Decode(data, stereo: false);
    Assert.That(pcm.Length, Is.EqualTo(2 * 8 * 28));
  }

  [Test]
  public void Decode_TrailingPartialGroup_IsIgnored() {
    var data = new byte[XaAdpcmCodec.SoundGroupSize + 17];
    var pcm = XaAdpcmCodec.Decode(data, stereo: false);
    Assert.That(pcm.Length, Is.EqualTo(8 * 28));
  }

  // ──────────── 5. Predictor (filter 1) hand-check ────────────

  /// <summary>
  /// Filter 1 has K0 = 60, K1 = 0. With shift 0 and nibble 1 (= 1 &lt;&lt; 12 = 4096) the first
  /// sample is 4096; the second adds <c>(4096*60 + 32) &gt;&gt; 6 = 3840</c> to its own 4096,
  /// giving 7936.
  /// </summary>
  [Test]
  public void Decode_Filter1_AppliesFirstOrderPredictor() {
    var units = new (int, int)[8];
    units[0] = (1, 0);
    var group = NewGroup(units);
    group[16] = 0x01; // unit 0 sample 0 = nibble 1
    group[20] = 0x01; // unit 0 sample 1 = nibble 1

    var left = new XaAdpcmCodec.History();
    var dummy = new XaAdpcmCodec.History();
    var output = new short[8 * 28];
    XaAdpcmCodec.DecodeGroup(group, stereo: false, ref left, ref dummy, output);

    Assert.That(output[0], Is.EqualTo((short)4096));
    Assert.That(output[1], Is.EqualTo((short)(4096 + ((4096 * 60 + 32) >> 6))));
  }

  // ──────────── 6. Round-trip closeness ────────────

  [Test]
  public void EncodeDecode_MonoSine_RoundTripsWithinTolerance() {
    const int count = 28 * 8 * 6; // whole groups
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 64) * 12000);

    var encoded = XaAdpcmCodec.Encode(pcm, stereo: false);
    var decoded = XaAdpcmCodec.Decode(encoded, stereo: false);

    Assert.That(decoded.Length, Is.GreaterThanOrEqualTo(count));
    var maxError = 0;
    for (var i = 0; i < count; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[i] - pcm[i]));
    Assert.That(maxError, Is.LessThan(1500), $"max abs error {maxError}");
  }

  [Test]
  public void EncodeDecode_StereoSine_ChannelsStayDistinct() {
    const int frames = 28 * 4 * 6; // whole stereo groups (4 units/channel/group)
    var pcm = new short[frames * 2];
    for (var i = 0; i < frames; ++i) {
      pcm[i * 2] = (short)(Math.Sin(i * 2 * Math.PI / 50) * 10000);      // left
      pcm[i * 2 + 1] = (short)(Math.Cos(i * 2 * Math.PI / 37) * 9000);   // right
    }

    var encoded = XaAdpcmCodec.Encode(pcm, stereo: true);
    var decoded = XaAdpcmCodec.Decode(encoded, stereo: true);

    var maxLeft = 0;
    var maxRight = 0;
    for (var i = 0; i < frames; ++i) {
      maxLeft = Math.Max(maxLeft, Math.Abs(decoded[i * 2] - pcm[i * 2]));
      maxRight = Math.Max(maxRight, Math.Abs(decoded[i * 2 + 1] - pcm[i * 2 + 1]));
    }
    Assert.That(maxLeft, Is.LessThan(2500), $"left max abs error {maxLeft}");
    Assert.That(maxRight, Is.LessThan(2500), $"right max abs error {maxRight}");
  }

  [Test]
  public void EncodeDecode_Silence_RoundTripsExactly() {
    var pcm = new short[28 * 8 * 2];
    var decoded = XaAdpcmCodec.Decode(XaAdpcmCodec.Encode(pcm, stereo: false), stereo: false);
    foreach (var s in decoded)
      Assert.That(s, Is.EqualTo((short)0));
  }

  [Test]
  public void Encode_Empty_ReturnsEmpty() {
    Assert.That(XaAdpcmCodec.Encode(ReadOnlySpan<short>.Empty, stereo: false).Length, Is.EqualTo(0));
  }

  // ──────────── 7. 8-bit decode smoke ────────────

  /// <summary>
  /// 8-bit mode carries 4 units of 28 eight-bit codes. Filter 0, shift 8 ⇒
  /// <c>s = signExtend8(code) &lt;&lt; 8 &gt;&gt; 8 = signExtend8(code)</c>. Unit 0 reads column
  /// byte 16, unit 1 reads 17, etc.
  /// </summary>
  [Test]
  public void Decode8Bit_Filter0_DecodesSignedBytes() {
    var group = new byte[XaAdpcmCodec.SoundGroupSize];
    // Units 0..3, filter 0 shift 8 at bytes 4..7.
    for (var u = 0; u < 4; ++u) group[4 + u] = 0x08;
    group[16] = 0x05;        // unit 0 sample 0 = 5
    group[17] = 0xFF;        // unit 1 sample 0 = -1
    group[18] = 0x7F;        // unit 2 sample 0 = 127

    var decoded = XaAdpcmCodec.Decode8Bit(group, stereo: false);
    Assert.That(decoded.Length, Is.EqualTo(4 * 28));
    Assert.That(decoded[0], Is.EqualTo((short)5));
    Assert.That(decoded[28], Is.EqualTo((short)(-1)));
    Assert.That(decoded[56], Is.EqualTo((short)127));
  }
}
