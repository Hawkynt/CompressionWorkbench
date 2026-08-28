#pragma warning disable CS1591
using Codec.Flac;

namespace Compression.Tests.Audio;

[TestFixture]
public class FlacEncoderTests {

  [TestCase(FlacSubframeMode.Verbatim)]
  [TestCase(FlacSubframeMode.Fixed0)]
  [TestCase(FlacSubframeMode.Fixed1)]
  [TestCase(FlacSubframeMode.Fixed2)]
  [TestCase(FlacSubframeMode.Fixed3)]
  [TestCase(FlacSubframeMode.Fixed4)]
  [TestCase(FlacSubframeMode.Auto)]
  public void Encode_AllSubframeModes_RoundTripBitExactly(FlacSubframeMode mode) {
    var pcm = Build16BitSignal(1503, 1);
    var encoded = FlacCodec.Encode(pcm, 44100, 1, blockSize: 256, compression: mode);
    var decoded = Decode16(encoded);

    Assert.That(encoded.AsSpan(0, 4).ToArray(), Is.EqualTo("fLaC"u8.ToArray()));
    Assert.That(decoded, Is.EqualTo(pcm));
    var info = FlacCodec.ReadAudioProperties(encoded);
    Assert.Multiple(() => {
      Assert.That(info.SampleRate, Is.EqualTo(44100));
      Assert.That(info.Channels, Is.EqualTo(1));
      Assert.That(info.BitsPerSample, Is.EqualTo(16));
      Assert.That(info.TotalSamples, Is.EqualTo(1503));
    });
  }

  [TestCase(FlacStereoMode.Independent)]
  [TestCase(FlacStereoMode.LeftSide)]
  [TestCase(FlacStereoMode.RightSide)]
  [TestCase(FlacStereoMode.MidSide)]
  [TestCase(FlacStereoMode.Auto)]
  public void Encode_AllStereoAssignments_RoundTripBitExactly(FlacStereoMode stereoMode) {
    var pcm = Build16BitSignal(777, 2);
    var encoded = FlacCodec.Encode(pcm, 48000, 2, blockSize: 192,
      compression: FlacSubframeMode.Auto, stereoMode: stereoMode);
    var decoded = Decode16(encoded);
    Assert.That(decoded, Is.EqualTo(pcm));
  }

  [Test]
  public void Encode_SixChannelIndependent_RoundTrips() {
    var pcm = Build16BitSignal(381, 6);
    var encoded = FlacCodec.Encode(pcm, 48000, 6, blockSize: 128,
      compression: FlacSubframeMode.Fixed2, stereoMode: FlacStereoMode.Independent);
    var decoded = Decode16(encoded);
    Assert.That(decoded, Is.EqualTo(pcm));
    Assert.That(FlacCodec.ReadAudioProperties(encoded).Channels, Is.EqualTo(6));
  }

  [Test]
  public void Encode_24Bit_RoundTripsBitExactly() {
    const int channels = 2;
    const int frames = 613;
    var pcm = new int[frames * channels];
    for (var i = 0; i < frames; ++i) {
      pcm[i * 2] = (int)(Math.Sin(i * 0.071) * 5_000_000);
      pcm[i * 2 + 1] = (int)(Math.Cos(i * 0.043) * 4_000_000);
    }

    var encoded = FlacCodec.Encode(pcm,
      new FlacEncoderOptions(96000, channels, 24, 144, FlacSubframeMode.Auto, FlacStereoMode.MidSide));
    var decoded = DecodeInt(encoded, 3);
    Assert.That(decoded, Is.EqualTo(pcm));
  }

  [Test]
  public void Encode_AutoCompression_IsSmallerThanVerbatimForPredictableSignal() {
    var pcm = new short[4096];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(i - 2048);

    var verbatim = FlacCodec.Encode(pcm, 44100, 1, 4096, FlacSubframeMode.Verbatim);
    var automatic = FlacCodec.Encode(pcm, 44100, 1, 4096, FlacSubframeMode.Auto);
    Assert.That(automatic.Length, Is.LessThan(verbatim.Length));
    Assert.That(Decode16(automatic), Is.EqualTo(pcm));
  }

  private static short[] Build16BitSignal(int frames, int channels) {
    var result = new short[frames * channels];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var a = Math.Sin((frame + channel * 7) * 0.061) * 19000;
        var b = Math.Cos((frame * (channel + 1) + 13) * 0.017) * 6000;
        result[frame * channels + channel] = (short)Math.Clamp((int)(a + b), short.MinValue, short.MaxValue);
      }
    return result;
  }

  private static short[] Decode16(byte[] flac) {
    using var input = new MemoryStream(flac, writable: false);
    using var output = new MemoryStream();
    FlacCodec.Decompress(input, output);
    var bytes = output.ToArray();
    var result = new short[bytes.Length / 2];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8);
    return result;
  }

  private static int[] DecodeInt(byte[] flac, int bytesPerSample) {
    using var input = new MemoryStream(flac, writable: false);
    using var output = new MemoryStream();
    FlacCodec.Decompress(input, output);
    var bytes = output.ToArray();
    var result = new int[bytes.Length / bytesPerSample];
    for (var i = 0; i < result.Length; ++i) {
      var p = i * bytesPerSample;
      var value = bytes[p] | bytes[p + 1] << 8 | bytes[p + 2] << 16;
      if ((value & 0x00800000) != 0) value |= unchecked((int)0xFF000000);
      result[i] = value;
    }
    return result;
  }
}
