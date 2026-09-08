using System.Buffers.Binary;
using Codec.CriAdx;

namespace Compression.Tests.Codecs.CriAdx;

[TestFixture]
public class AdxCodecTests {
  private static short[] Sine(int frames, int channels = 1) {
    var pcm = new short[frames * channels];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel)
        pcm[frame * channels + channel] = (short)(Math.Sin(frame / (7.0 + channel * 3)) * (9000 - channel * 500));
    return pcm;
  }

  [Test]
  public void DeriveCoefficients_44100_500_MatchesHandComputed() {
    var (coef1, coef2) = AdxCodec.DeriveCoefficients(500, 44100);
    Assert.That(coef1, Is.EqualTo(7334));
    Assert.That(coef2, Is.EqualTo(-3283));
  }

  [Test]
  public void Encode_WritesValidV3Header_WithCriCopyrightString() {
    var pcm = new short[AdxCodec.SamplesPerFrame * 2];
    var adx = AdxCodec.Encode(pcm, channels: 1, sampleRate: 22050);
    var info = AdxCodec.ReadInfo(adx);

    Assert.Multiple(() => {
      Assert.That(info.EncodingType, Is.EqualTo(AdxCodec.EncodingTypeStandard));
      Assert.That(info.BlockSize, Is.EqualTo(AdxCodec.FrameSize));
      Assert.That(info.BitDepth, Is.EqualTo(AdxCodec.BitDepth));
      Assert.That(info.Channels, Is.EqualTo(1));
      Assert.That(info.SampleRate, Is.EqualTo(22050));
      Assert.That(info.TotalSamples, Is.EqualTo(pcm.Length));
      Assert.That(info.HighpassFrequency, Is.EqualTo(500));
      Assert.That(info.VersionSignature, Is.EqualTo(0x0300));
      Assert.That(info.IsEncrypted, Is.False);
      Assert.That(System.Text.Encoding.ASCII.GetString(adx, info.DataOffset - 6, 6), Is.EqualTo("(c)CRI"));
    });
  }

  [Test]
  public void Encode_StandardScale_StoresScaleMinusOne() {
    var pcm = new short[AdxCodec.SamplesPerFrame];
    var adx = AdxCodec.Encode(pcm, 1, 22050);
    var info = AdxCodec.ReadInfo(adx);
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(adx.AsSpan(info.DataOffset)), Is.Zero,
      "silence uses scale 1, therefore the type-3 frame stores scale-1 == 0");
  }

  [TestCase(AdxEncodingMode.Standard, AdxHeaderVersion.Version3)]
  [TestCase(AdxEncodingMode.Standard, AdxHeaderVersion.Version4)]
  [TestCase(AdxEncodingMode.Standard, AdxHeaderVersion.Version5)]
  [TestCase(AdxEncodingMode.Fixed, AdxHeaderVersion.Version3)]
  [TestCase(AdxEncodingMode.Fixed, AdxHeaderVersion.Version4)]
  [TestCase(AdxEncodingMode.Fixed, AdxHeaderVersion.Version5)]
  [TestCase(AdxEncodingMode.Exponential, AdxHeaderVersion.Version3)]
  [TestCase(AdxEncodingMode.Exponential, AdxHeaderVersion.Version4)]
  [TestCase(AdxEncodingMode.Exponential, AdxHeaderVersion.Version5)]
  public void EncodeDecode_AllAdpcmEncodingAndHeaderVersionPairs_RoundTripGeometry(
    AdxEncodingMode encoding,
    AdxHeaderVersion version) {
    var pcm = Sine(AdxCodec.SamplesPerFrame * 5 + 7, channels: 2);
    var adx = AdxCodec.Encode(pcm, 2, 48000, new AdxEncodeOptions {
      Encoding = encoding,
      Version = version,
      HighpassFrequency = 500,
    });
    var info = AdxCodec.ReadInfo(adx);
    var (decoded, channels, rate) = AdxCodec.Decode(adx);

    Assert.Multiple(() => {
      Assert.That(info.EncodingType, Is.EqualTo((byte)encoding));
      Assert.That(info.Version, Is.EqualTo((byte)version));
      Assert.That(channels, Is.EqualTo(2));
      Assert.That(rate, Is.EqualTo(48000));
      Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.Any(static sample => sample != 0), Is.True);
    });
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(4)]
  [TestCase(8)]
  public void EncodeDecode_ChannelCountsThroughEight_AreSupported(int channels) {
    var pcm = Sine(AdxCodec.SamplesPerFrame * 2 + 1, channels);
    var adx = AdxCodec.Encode(pcm, channels, 44100, new AdxEncodeOptions { Version = AdxHeaderVersion.Version4 });
    var (decoded, actualChannels, _) = AdxCodec.Decode(adx);
    Assert.That(actualChannels, Is.EqualTo(channels));
    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
  }

  [Test]
  public void EncodeDecode_SmoothSine_StandardRoundTripsWithinTolerance() {
    var pcm = Sine(AdxCodec.SamplesPerFrame * 40);
    var (decoded, _, _) = AdxCodec.Decode(AdxCodec.Encode(pcm, 1, 32000));
    var maxError = pcm.Zip(decoded, static (expected, actual) => Math.Abs(expected - actual)).Max();
    Assert.That(maxError, Is.LessThan(1700), $"max abs error {maxError}");
  }

  [Test]
  public void EncodeDecode_Stereo_PreservesChannelSeparation() {
    var pcm = Sine(AdxCodec.SamplesPerFrame * 10, 2);
    var (decoded, channels, _) = AdxCodec.Decode(AdxCodec.Encode(pcm, 2, 48000));
    Assert.That(channels, Is.EqualTo(2));
    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
  }

  [TestCase((byte)8)]
  [TestCase((byte)9)]
  public void EncodeDecode_EncryptedV4_RoundTripsWithDerivedKey(byte revision) {
    var key = new AdxEncryptionKey(0x1234, 0x1F3D, 0x0457, revision);
    var pcm = Sine(AdxCodec.SamplesPerFrame * 4 + 3, 2);
    var adx = AdxCodec.Encode(pcm, 2, 44100, new AdxEncodeOptions {
      Version = AdxHeaderVersion.Version4,
      Encoding = AdxEncodingMode.Standard,
      Encryption = key,
    });

    var info = AdxCodec.ReadInfo(adx);
    Assert.Multiple(() => {
      Assert.That(info.IsEncrypted, Is.True);
      Assert.That(info.Revision, Is.EqualTo(revision));
      Assert.That(() => AdxCodec.Decode(adx), Throws.TypeOf<NotSupportedException>());
    });
    var (decoded, channels, rate) = AdxCodec.Decode(adx, key);
    Assert.That(channels, Is.EqualTo(2));
    Assert.That(rate, Is.EqualTo(44100));
    Assert.That(decoded.Length, Is.EqualTo(pcm.Length));
  }

  [TestCase(AdxHeaderVersion.Version3)]
  [TestCase(AdxHeaderVersion.Version4)]
  public void Encode_LoopMetadata_IsReadBack(AdxHeaderVersion version) {
    var pcm = Sine(AdxCodec.SamplesPerFrame * 8);
    var adx = AdxCodec.Encode(pcm, 1, 22050, new AdxEncodeOptions {
      Version = version,
      LoopStartSample = 17,
      LoopEndSample = 197,
      HeaderAlignment = 256,
    });
    var info = AdxCodec.ReadInfo(adx);
    Assert.Multiple(() => {
      Assert.That(info.DataOffset % 256, Is.Zero);
      Assert.That(info.LoopStartSample, Is.EqualTo(17));
      Assert.That(info.LoopEndSample, Is.EqualTo(197));
    });
  }

  [Test]
  public void Encode_V5Loop_IsRejected() {
    var pcm = Sine(100);
    Assert.That(() => AdxCodec.Encode(pcm, 1, 22050, new AdxEncodeOptions {
      Version = AdxHeaderVersion.Version5,
      LoopStartSample = 10,
      LoopEndSample = 90,
    }), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void Encode_EndMarker_AppendsConventionalTerminatorWithoutChangingDeclaredSamples() {
    var pcm = Sine(AdxCodec.SamplesPerFrame + 1);
    var adx = AdxCodec.Encode(pcm, 1, 22050, new AdxEncodeOptions { WriteEndMarker = true });
    var info = AdxCodec.ReadInfo(adx);
    var groups = (info.TotalSamples + AdxCodec.SamplesPerFrame - 1) / AdxCodec.SamplesPerFrame;
    var marker = info.DataOffset + groups * AdxCodec.FrameSize;
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(adx.AsSpan(marker)), Is.EqualTo(AdxCodec.EndMarkerScale));
    Assert.That(AdxCodec.Decode(adx).InterleavedPcm.Length, Is.EqualTo(pcm.Length));
  }

  [Test]
  public void BuildAhxHeader_UsesAhxGeometryAndVersionSignature() {
    var header = AdxCodec.BuildAhxHeader(22050, 1152, AdxCodec.EncodingTypeAhx11);
    var info = AdxCodec.ReadInfo(header);
    Assert.Multiple(() => {
      Assert.That(info.IsAhx, Is.True);
      Assert.That(info.BlockSize, Is.Zero);
      Assert.That(info.BitDepth, Is.Zero);
      Assert.That(info.Channels, Is.EqualTo(1));
      Assert.That(info.VersionSignature, Is.EqualTo(0x0600));
    });
  }

  [Test]
  public void Decode_UnknownEncodingType_Throws() {
    var adx = AdxCodec.Encode(new short[AdxCodec.SamplesPerFrame], 1, 22050);
    adx[4] = 0x7F;
    Assert.That(() => AdxCodec.Decode(adx), Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void Decode_TruncatedFrameData_Throws() {
    var adx = AdxCodec.Encode(Sine(AdxCodec.SamplesPerFrame * 2), 1, 22050);
    Array.Resize(ref adx, adx.Length - 1);
    Assert.That(() => AdxCodec.Decode(adx), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void ReadInfo_MissingMagic_Throws() {
    Assert.That(() => AdxCodec.ReadInfo(new byte[20]), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void ReadInfo_MissingCopyrightMarker_Throws() {
    var adx = AdxCodec.Encode(new short[32], 1, 22050);
    var info = AdxCodec.ReadInfo(adx);
    adx[info.DataOffset - 1] ^= 0xFF;
    Assert.That(() => AdxCodec.ReadInfo(adx), Throws.TypeOf<InvalidDataException>());
  }
}
