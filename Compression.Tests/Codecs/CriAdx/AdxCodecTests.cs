using Codec.CriAdx;

namespace Compression.Tests.Codecs.CriAdx;

[TestFixture]
public class AdxCodecTests {

  // ──────────── 1. Predictor coefficient math ────────────

  /// <summary>
  /// The standard ADX coefficients for 44100 Hz / 500 Hz high-pass derive from
  /// z = cos(2π·500/44100), a = √2 − z, b = √2 − 1, c = (a − √((a+b)(a−b)))/b,
  /// coef1 = ⌊c·8192⌋, coef2 = ⌊−c²·4096⌋. Hand-computing those yields 0x1CA6 / 0x7332's
  /// signed values 7334 and −3284.
  /// </summary>
  [Test]
  public void DeriveCoefficients_44100_500_MatchesHandComputed() {
    var (coef1, coef2) = AdxCodec.DeriveCoefficients(500, 44100);
    Assert.That(coef1, Is.EqualTo(7334));
    Assert.That(coef2, Is.EqualTo(-3284));
  }

  // ──────────── 2. Header round-trip ────────────

  [Test]
  public void Encode_WritesValidHeader_WithCriCopyrightString() {
    var pcm = new short[AdxCodec.SamplesPerFrame * 2];
    var adx = AdxCodec.Encode(pcm, channels: 1, sampleRate: 22050);

    // Magic high bit set.
    Assert.That((adx[0] & 0x80) != 0, Is.True);

    var info = AdxCodec.ReadInfo(adx);
    Assert.That(info.EncodingType, Is.EqualTo(AdxCodec.EncodingTypeStandard));
    Assert.That(info.BlockSize, Is.EqualTo(AdxCodec.FrameSize));
    Assert.That(info.BitDepth, Is.EqualTo(AdxCodec.BitDepth));
    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.SampleRate, Is.EqualTo(22050));
    Assert.That(info.TotalSamples, Is.EqualTo(pcm.Length));
    Assert.That(info.HighpassFrequency, Is.EqualTo(500));
    Assert.That(info.Version, Is.EqualTo(3));
    Assert.That(info.IsEncrypted, Is.False);
    Assert.That(info.IsStandard, Is.True);

    // The "(c)CRI" string sits at copyrightOffset - 2.
    var copyrightOffset = info.DataOffset - 4;
    var cri = System.Text.Encoding.ASCII.GetString(adx, copyrightOffset - 2, 6);
    Assert.That(cri, Is.EqualTo("(c)CRI"));
  }

  [Test]
  public void Encode_TotalSamples_DrivesDecodedLength() {
    const int samples = AdxCodec.SamplesPerFrame * 3 + 5; // partial final frame
    var pcm = new short[samples];
    var adx = AdxCodec.Encode(pcm, channels: 1, sampleRate: 16000);

    var (decoded, channels, rate) = AdxCodec.Decode(adx);
    Assert.That(channels, Is.EqualTo(1));
    Assert.That(rate, Is.EqualTo(16000));
    Assert.That(decoded.Length, Is.EqualTo(samples));
  }

  // ──────────── 3. Encode → decode round-trip (lossy, tolerance) ────────────

  [Test]
  public void EncodeDecode_SmoothSine_RoundTripsWithinTolerance() {
    const int count = AdxCodec.SamplesPerFrame * 40;
    var pcm = new short[count];
    for (var i = 0; i < count; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 80) * 10000);

    var adx = AdxCodec.Encode(pcm, channels: 1, sampleRate: 32000);
    var (decoded, _, _) = AdxCodec.Decode(adx);

    Assert.That(decoded.Length, Is.EqualTo(count));

    var maxError = 0;
    for (var i = 0; i < count; ++i)
      maxError = Math.Max(maxError, Math.Abs(decoded[i] - pcm[i]));

    Assert.That(maxError, Is.LessThan(1500), $"max abs error {maxError}");
  }

  [Test]
  public void EncodeDecode_Silence_RoundTripsExactly() {
    var pcm = new short[AdxCodec.SamplesPerFrame * 3];
    var (decoded, _, _) = AdxCodec.Decode(AdxCodec.Encode(pcm, channels: 1, sampleRate: 44100));
    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
    foreach (var s in decoded)
      Assert.That(s, Is.EqualTo((short)0));
  }

  // ──────────── 4. Stereo interleave ordering ────────────

  [Test]
  public void EncodeDecode_Stereo_PreservesChannelSeparation() {
    const int frames = AdxCodec.SamplesPerFrame * 10;
    var pcm = new short[frames * 2];
    for (var i = 0; i < frames; ++i) {
      pcm[i * 2] = (short)(Math.Sin(i / 7.0) * 8000);      // left
      pcm[i * 2 + 1] = (short)(Math.Sin(i / 11.0) * 4000); // right
    }

    var adx = AdxCodec.Encode(pcm, channels: 2, sampleRate: 48000);
    var (decoded, channels, _) = AdxCodec.Decode(adx);

    Assert.That(channels, Is.EqualTo(2));
    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));

    var maxLeft = 0;
    var maxRight = 0;
    for (var i = 0; i < frames; ++i) {
      maxLeft = Math.Max(maxLeft, Math.Abs(decoded[i * 2] - pcm[i * 2]));
      maxRight = Math.Max(maxRight, Math.Abs(decoded[i * 2 + 1] - pcm[i * 2 + 1]));
    }
    Assert.That(maxLeft, Is.LessThan(1500));
    Assert.That(maxRight, Is.LessThan(1500));
  }

  // ──────────── 5. Rejection paths ────────────

  [Test]
  public void Decode_EncryptedFlag_Throws() {
    var pcm = new short[AdxCodec.SamplesPerFrame];
    var adx = AdxCodec.Encode(pcm, channels: 1, sampleRate: 22050);
    adx[19] |= 0x08; // set encrypted flag

    Assert.That(() => AdxCodec.Decode(adx), Throws.TypeOf<NotSupportedException>());
    Assert.That(AdxCodec.ReadInfo(adx).IsEncrypted, Is.True);
  }

  [Test]
  public void Decode_NonStandardEncodingType_Throws() {
    var pcm = new short[AdxCodec.SamplesPerFrame];
    var adx = AdxCodec.Encode(pcm, channels: 1, sampleRate: 22050);
    adx[4] = 2; // AHX / non-standard encoding type

    Assert.That(() => AdxCodec.Decode(adx), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void ReadInfo_MissingMagic_Throws() {
    var bogus = new byte[20];
    Assert.That(() => AdxCodec.ReadInfo(bogus), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Decode_EndMarkerFrame_StopsAddingDeltas() {
    // A lone frame with a 0x8001 scale marker must not emit fresh deltas; with zero
    // history this means the covered samples decode to silence.
    var pcm = new short[AdxCodec.SamplesPerFrame];
    var adx = AdxCodec.Encode(pcm, channels: 1, sampleRate: 22050);
    var info = AdxCodec.ReadInfo(adx);
    // Force the single frame's scale word to the end-of-stream marker.
    adx[info.DataOffset] = 0x80;
    adx[info.DataOffset + 1] = 0x01;

    var (decoded, _, _) = AdxCodec.Decode(adx);
    foreach (var s in decoded)
      Assert.That(s, Is.EqualTo((short)0));
  }
}
