#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.WavPack;

namespace Compression.Tests.Codecs.WavPack;

[TestFixture]
public class WavPackCodecTests {

  private static byte[] Encode(byte[] pcm, int channels, int sampleRate, int bits) {
    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    WavPackCodec.Compress(input, output, channels, sampleRate, bits);
    return output.ToArray();
  }

  private static byte[] Decode(byte[] wv) {
    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    WavPackCodec.Decompress(input, output);
    return output.ToArray();
  }

  private static byte[] RoundTrip(byte[] pcm, int channels, int sampleRate, int bits)
    => Decode(Encode(pcm, channels, sampleRate, bits));

  private static byte[] EncodeFloat(byte[] pcm, int channels, int sampleRate) {
    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    WavPackCodec.Compress(input, output, channels, sampleRate, bitsPerSample: 32, isFloat: true);
    return output.ToArray();
  }

  private static byte[] RoundTripFloat(byte[] pcm, int channels, int sampleRate)
    => Decode(EncodeFloat(pcm, channels, sampleRate));

  // Packs an interleaved sequence of float samples into a raw little-endian PCM blob.
  private static byte[] FloatPcm(params float[] samples) {
    var pcm = new byte[samples.Length * 4];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 4), samples[i]);
    return pcm;
  }

  // Reinterprets a raw float-PCM blob as a float array for exact bit comparison.
  private static float[] AsFloats(byte[] pcm) {
    var f = new float[pcm.Length / 4];
    for (var i = 0; i < f.Length; ++i)
      f[i] = BinaryPrimitives.ReadSingleLittleEndian(pcm.AsSpan(i * 4));
    return f;
  }

  // ── Lossless round-trips ─────────────────────────────────────────────────────

  [Test]
  public void RoundTrip_16BitStereo_SineAndNoise_IsLossless() {
    const int frames = 8192;
    var rng = new Random(1234);
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i) {
      var l = (short)(Math.Sin(i * 0.05) * 20000 + (rng.Next(2001) - 1000));
      var r = (short)(Math.Cos(i * 0.03) * 15000 + (rng.Next(401) - 200));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), l);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), r);
    }

    Assert.That(RoundTrip(pcm, 2, 44100, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_16BitMono_IsLossless() {
    const int frames = 6000;
    var rng = new Random(99);
    var pcm = new byte[frames * 2];
    for (var i = 0; i < frames; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2),
        (short)(Math.Sin(i * 0.1) * 9000 + (rng.Next(401) - 200)));

    Assert.That(RoundTrip(pcm, 1, 22050, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_16BitStereo_PureNoise_IsLossless() {
    const int frames = 4000;
    var rng = new Random(2024);
    var pcm = new byte[frames * 2 * 2];
    rng.NextBytes(pcm);
    Assert.That(RoundTrip(pcm, 2, 48000, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_16BitStereo_Silence_IsLossless() {
    const int frames = 3000;
    var pcm = new byte[frames * 2 * 2]; // all zero
    Assert.That(RoundTrip(pcm, 2, 44100, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_8BitMono_IsLossless() {
    const int frames = 5000;
    var rng = new Random(7);
    var pcm = new byte[frames];
    for (var i = 0; i < frames; ++i)
      pcm[i] = (byte)(128 + (int)(Math.Sin(i * 0.1) * 60) + (rng.Next(11) - 5));

    Assert.That(RoundTrip(pcm, 1, 8000, 8), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_24BitStereo_IsLossless() {
    const int frames = 3000;
    var rng = new Random(55);
    var pcm = new byte[frames * 2 * 3];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < 2; ++c) {
        var v = (int)(Math.Sin(i * 0.02 + c) * 4_000_000) + rng.Next(20001) - 10000;
        var off = (i * 2 + c) * 3;
        pcm[off] = (byte)(v & 0xFF);
        pcm[off + 1] = (byte)((v >> 8) & 0xFF);
        pcm[off + 2] = (byte)((v >> 16) & 0xFF);
      }

    Assert.That(RoundTrip(pcm, 2, 96000, 24), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_5Point1SixChannel_IsLossless() {
    const int frames = 2000;
    const int ch = 6;
    var rng = new Random(321);
    var pcm = new byte[frames * ch * 2];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < ch; ++c)
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * ch + c) * 2),
          (short)(Math.Sin(i * (0.01 * (c + 1))) * 12000 + (rng.Next(801) - 400)));

    // Six channels => three stereo sub-blocks chained, final flagged.
    Assert.That(RoundTrip(pcm, ch, 48000, 16), Is.EqualTo(pcm));
  }

  // ── IEEE float round-trips ────────────────────────────────────────────────────

  [Test]
  public void RoundTripFloat_MonoNormalValues_IsLossless() {
    const int frames = 4096;
    var pcm = new byte[frames * 4];
    for (var i = 0; i < frames; ++i)
      BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 4),
        (float)(Math.Sin(i * 0.05) * 0.8 + Math.Cos(i * 0.013) * 0.1));

    Assert.That(RoundTripFloat(pcm, 1, 48000), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTripFloat_StereoNormalValues_IsLossless() {
    const int frames = 4096;
    var pcm = new byte[frames * 2 * 4];
    var rng = new Random(7);
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 8),
        (float)(Math.Sin(i * 0.05) * 0.5 + (rng.NextDouble() - 0.5) * 1e-3));
      BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 8 + 4),
        (float)(Math.Cos(i * 0.031) * 0.25));
    }

    Assert.That(RoundTripFloat(pcm, 2, 44100), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTripFloat_FullRangeExponents_IsLossless() {
    // Span tiny to huge magnitudes so float_max_exp and the per-sample shift cover
    // the whole exponent range.
    var values = new List<float>();
    for (var e = -60; e <= 60; ++e) {
      values.Add((float)Math.ScaleB(1.0, e));
      values.Add((float)-Math.ScaleB(1.3, e));
    }
    // pad to an even count for a clean stereo frame test
    if ((values.Count & 1) != 0) values.Add(0f);
    var pcm = FloatPcm([.. values]);

    Assert.That(RoundTripFloat(pcm, 1, 48000), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTripFloat_PositiveAndNegativeZeros_PreservesSignOfZero() {
    var pcm = FloatPcm(0f, -0f, 0f, -0f, 1.5f, -0f, 0f, -2.25f);
    var decoded = RoundTripFloat(pcm, 1, 44100);

    Assert.That(decoded, Is.EqualTo(pcm), "raw bit patterns (incl. sign of zero) must match");
    // Explicitly assert the negative-zero bit survived (it differs from +0 only in sign).
    var f = AsFloats(decoded);
    Assert.That(BitConverter.SingleToInt32Bits(f[1]), Is.EqualTo(unchecked((int)0x80000000)));
  }

  [Test]
  public void RoundTripFloat_Denormals_IsLossless() {
    // Subnormal floats have a zero exponent and a non-zero mantissa.
    var pcm = FloatPcm(
      float.Epsilon, -float.Epsilon, 3 * float.Epsilon,
      BitConverter.Int32BitsToSingle(0x00000001),
      BitConverter.Int32BitsToSingle(0x007FFFFF),
      BitConverter.Int32BitsToSingle(0x00400000),
      1.0f, 0.5f);

    Assert.That(RoundTripFloat(pcm, 1, 96000), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTripFloat_NaNAndInfinities_IsLossless() {
    var pcm = FloatPcm(
      float.PositiveInfinity, float.NegativeInfinity, float.NaN,
      BitConverter.Int32BitsToSingle(0x7FC00001), // a specific quiet NaN payload
      1.0f, -1.0f, 123.5f, -0.0f);
    var decoded = RoundTripFloat(pcm, 1, 44100);

    Assert.That(decoded, Is.EqualTo(pcm), "inf/NaN bit patterns must be preserved exactly");
  }

  [Test]
  public void RoundTripFloat_AllZeros_IsLossless() {
    var pcm = new byte[2048 * 4]; // all +0.0
    Assert.That(RoundTripFloat(pcm, 1, 44100), Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTripFloat_IntegerDerivedFloats_IsLossless() {
    // Floats whose mantissas have trailing-zero structure exercise the
    // float_shift magnitude-reduction path (no extension stream required).
    var values = new float[512];
    for (var i = 0; i < values.Length; ++i)
      values[i] = (i % 257) - 128; // exact small integers as floats
    var pcm = FloatPcm(values);

    Assert.That(RoundTripFloat(pcm, 1, 48000), Is.EqualTo(pcm));
  }

  [Test]
  public void ReadStreamInfo_FloatStream_ReportsIsFloat() {
    var pcm = FloatPcm(0.1f, 0.2f, 0.3f, 0.4f);
    var wv = EncodeFloat(pcm, 2, 44100);
    using var ms = new MemoryStream(wv);
    var info = WavPackCodec.ReadStreamInfo(ms);

    Assert.Multiple(() => {
      Assert.That(info.IsFloat, Is.True);
      Assert.That(info.BitsPerSample, Is.EqualTo(32));
      Assert.That(info.Channels, Is.EqualTo(2));
    });
  }

  [Test]
  public void FloatStream_CarriesFloatInfoSubBlock() {
    var pcm = FloatPcm(0.1f, -0.2f, 3e-30f, 1234.5f);
    var wv = EncodeFloat(pcm, 1, 44100);
    // FLOAT_INFO is raw id 0x08; locate it via the body sub-block walk helper.
    var idx = FindSubBlockPayload(wv, 0x08, out var size);
    Assert.That(idx, Is.GreaterThan(0), "FLOAT_INFO sub-block present");
    Assert.That(size, Is.EqualTo(4), "FLOAT_INFO is exactly four bytes");
    // byte[3] is float_norm_exp, which the encoder sets to 127 (the +/-1.0 norm).
    Assert.That(wv[idx + 3], Is.EqualTo(127));
  }

  [Test]
  public void FloatStream_TruncatedBitstream_ToleratesAllOnesEof() {
    // Shrink the wvx extension stream in place (keeping the block frame valid) so the
    // float reconstruction reads past its end; the bit reader's all-ones EOF
    // convention must let it terminate without throwing rather than crashing.
    var pcm = FloatPcm(0.1f, -0.2f, 0.3f, -0.4f, 0.5f, -0.6f, 0.7f, -0.8f);
    var wv = EncodeFloat(pcm, 1, 44100);
    // Zero the last few payload bytes (within the framed block) to simulate a
    // corrupt/short tail; decode must still complete.
    for (var i = wv.Length - 4; i < wv.Length; ++i)
      wv[i] = 0xFF;
    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.Nothing);
  }

  [Test]
  public void Float32Compress_RejectsNonFloatBitDepthMismatch() {
    using var input = new MemoryStream(new byte[16]);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Compress(input, output, 1, 44100, bitsPerSample: 16, isFloat: true),
      Throws.TypeOf<ArgumentException>());
  }

  // ── Header / flag parsing ─────────────────────────────────────────────────────

  [Test]
  public void Encoded_Stream_StartsWith_WvpkMagic() {
    var pcm = new byte[1000 * 4];
    var wv = Encode(pcm, 2, 44100, 16);
    Assert.That(System.Text.Encoding.ASCII.GetString(wv, 0, 4), Is.EqualTo("wvpk"));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wv.AsSpan(8)), Is.EqualTo(0x0410), "version");
  }

  [Test]
  public void ReadStreamInfo_ReturnsHeaderFields_Stereo() {
    const int frames = 1234;
    var pcm = new byte[frames * 2 * 2];
    var wv = Encode(pcm, 2, 44100, 16);

    using var ms = new MemoryStream(wv);
    var info = WavPackCodec.ReadStreamInfo(ms);

    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.SampleRate, Is.EqualTo(44100));
    Assert.That(info.BitsPerSample, Is.EqualTo(16));
    Assert.That(info.SampleCount, Is.EqualTo(frames));
  }

  [Test]
  public void ReadStreamInfo_ReturnsHeaderFields_SixChannel() {
    const int frames = 500;
    var pcm = new byte[frames * 6 * 2];
    var wv = Encode(pcm, 6, 48000, 16);

    using var ms = new MemoryStream(wv);
    var info = WavPackCodec.ReadStreamInfo(ms);

    Assert.That(info.Channels, Is.EqualTo(6));
    Assert.That(info.SampleRate, Is.EqualTo(48000));
    Assert.That(info.SampleCount, Is.EqualTo(frames));
  }

  // ── Rejection paths ───────────────────────────────────────────────────────────

  [Test]
  public void CorruptedMagic_Throws() {
    var pcm = new byte[1000 * 4];
    var wv = Encode(pcm, 2, 44100, 16);
    wv[0] = (byte)'X';

    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void HybridFlag_Rejected() {
    var pcm = new byte[1000 * 4];
    var wv = Encode(pcm, 2, 44100, 16);
    // Set hybrid bit (bit 3) in the flags word at offset 24.
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(wv.AsSpan(24));
    flags |= 0x8;
    BinaryPrimitives.WriteUInt32LittleEndian(wv.AsSpan(24), flags);

    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void FloatFlag_Rejected() {
    var pcm = new byte[1000 * 4];
    var wv = Encode(pcm, 2, 44100, 16);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(wv.AsSpan(24));
    flags |= 0x80; // float data
    BinaryPrimitives.WriteUInt32LittleEndian(wv.AsSpan(24), flags);

    using var input = new MemoryStream(wv);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void NonWavPackInput_Throws() {
    var junk = new byte[128];
    new Random(1).NextBytes(junk);
    junk[0] = (byte)'X'; junk[1] = (byte)'Y';

    using var input = new MemoryStream(junk);
    using var output = new MemoryStream();
    Assert.That(() => WavPackCodec.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  // ── Reference wp_log2 / wp_exp2s port faithfulness ───────────────────────────
  // Fixed points taken directly from the reference entropy_utils.c tables.

  [Test]
  public void WpLog2_KnownFixedPoints_MatchReference() {
    Assert.Multiple(() => {
      Assert.That(WavPackCodec.WpLog2(1), Is.EqualTo(256));
      Assert.That(WavPackCodec.WpLog2(2), Is.EqualTo(512));
      Assert.That(WavPackCodec.WpLog2(16), Is.EqualTo(1280));
      Assert.That(WavPackCodec.WpLog2(256), Is.EqualTo(2304));
      Assert.That(WavPackCodec.WpLog2(65535), Is.EqualTo(4352));
    });
  }

  [Test]
  public void WpExp2S_KnownFixedPoints_MatchReference() {
    Assert.Multiple(() => {
      Assert.That(WavPackCodec.WpExp2S(0), Is.EqualTo(0));
      Assert.That(WavPackCodec.WpExp2S(256), Is.EqualTo(1));
      Assert.That(WavPackCodec.WpExp2S(512), Is.EqualTo(2));
      Assert.That(WavPackCodec.WpExp2S(2048), Is.EqualTo(128));
      Assert.That(WavPackCodec.WpExp2S(-256), Is.EqualTo(-1));
    });
  }

  [Test]
  public void WpExp2S_OfWpLog2_RoundTrips_WithinReferenceError() {
    // The reference guarantees round-trip error within 1 part in 225, with the only
    // exceptions being +/-115 and +/-195 (which error by exactly 1).
    for (var x = 1; x < 200000; x = x + 1 + x / 64) {
      var back = WavPackCodec.WpExp2S(WavPackCodec.WpLog2((uint)x));
      var tolerance = Math.Max(1, x / 225 + 1);
      Assert.That(Math.Abs(back - x), Is.LessThanOrEqualTo(tolerance), $"x={x}");
    }
  }

  [Test]
  public void WpLog2S_IsOddSymmetric() {
    foreach (var x in new[] { 1, 2, 50, 1000, 32767 })
      Assert.That(WavPackCodec.WpLog2S(-x), Is.EqualTo(-WavPackCodec.WpLog2S(x)));
  }

  // ── New reference-path coverage ──────────────────────────────────────────────

  [Test]
  public void AllZeroStereoBlock_UsesZeroRunCoding_AndIsTiny() {
    // An all-zero stereo block must now be coded via the zeros_acc run path: the
    // bitstream sub-block should be a tiny fraction of the per-sample cost.
    const int frames = 20000;
    var pcm = new byte[frames * 2 * 2]; // silence
    var wv = Encode(pcm, 2, 44100, 16);

    // The whole encoded stream (header + sub-blocks + bitstream) must be far below
    // the ~1.2 bits/sample a non-run coder would need (>= frames*2*0.15 bytes).
    Assert.That(wv.Length, Is.LessThan(frames * 2 / 4),
      "silence should collapse via zeros_acc run-length coding");
    Assert.That(Decode(wv), Is.EqualTo(pcm));
  }

  [Test]
  public void LongConstantLargeResidual_ExercisesHoldingOneChains_RoundTrips() {
    // A high-amplitude square wave produces long unary "ones" runs, exercising the
    // holding_one chains and the LIMIT_ONES escape; it must still round-trip.
    const int frames = 8000;
    var pcm = new byte[frames * 2];
    for (var i = 0; i < frames; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2),
        (short)((i / 7 & 1) == 0 ? 30000 : -30000));

    Assert.That(RoundTrip(pcm, 1, 44100, 16), Is.EqualTo(pcm));
  }

  [Test]
  public void Silence_EncodesSmallerThanNoise_ForSameLength() {
    const int frames = 16000;
    var silence = new byte[frames * 2 * 2];
    var noise = new byte[frames * 2 * 2];
    new Random(42).NextBytes(noise);

    var silentWv = Encode(silence, 2, 44100, 16);
    var noiseWv = Encode(noise, 2, 44100, 16);

    Assert.That(silentWv.Length, Is.LessThan(noiseWv.Length / 10),
      "the zeros_acc run path must make silence dramatically smaller than noise");
  }

  [Test]
  public void TamperingDecorrWeightsSubBlock_ChangesDecodedOutput() {
    // The decoder honours the stored decorr-weights sub-block; corrupting it must
    // change the decoded samples (proving weights are applied, not ignored).
    const int frames = 4000;
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(Math.Sin(i * 0.05) * 12000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(Math.Cos(i * 0.04) * 9000));
    }
    var wv = Encode(pcm, 2, 44100, 16);
    var clean = Decode(wv);

    // Find the 0x03 (decorr-weights) sub-block in the first block body and flip a
    // weight byte to a large non-zero value.
    var tampered = (byte[])wv.Clone();
    var idx = FindSubBlockPayload(tampered, 0x03, out var size);
    Assert.That(idx, Is.GreaterThan(0), "decorr-weights sub-block present");
    Assert.That(size, Is.GreaterThan(0));
    tampered[idx] = 0x40; // a large restore_weight input

    var tamperedOut = Decode(tampered);
    Assert.That(tamperedOut, Is.Not.EqualTo(clean),
      "altering the decorr-weights sub-block must change the decoded output");
  }

  // Locates the payload offset of the first metadata sub-block with the given raw
  // id inside the first wvpk block; returns -1 if absent.
  private static int FindSubBlockPayload(byte[] wv, int rawId, out int size) {
    size = 0;
    var ckSize = BinaryPrimitives.ReadUInt32LittleEndian(wv.AsSpan(4));
    var bodyEnd = 8 + (int)ckSize;
    var o = 32;
    while (o < bodyEnd && o < wv.Length) {
      // id byte: low six bits the id, bit 6 odd size, bit 7 three-word size field
      var id = wv[o++];
      int s;
      if ((id & 0x80) != 0) {
        s = (wv[o] | (wv[o + 1] << 8) | (wv[o + 2] << 16)) << 1;
        o += 3;
      } else {
        s = wv[o++] << 1;
      }
      if ((id & 0x40) != 0) s -= 1;
      var payloadStart = o;
      if ((id & 0x3F) == rawId) {
        size = s;
        return payloadStart;
      }
      o = payloadStart + s + (s & 1);
    }
    return -1;
  }
}
