#pragma warning disable CS1591
using Codec.Opus;
using Concentus.Enums;
using ConcentusOpusMode = Concentus.Enums.OpusMode;

namespace Compression.Tests.Audio;

[TestFixture]
public class OpusEncoderTests {

  [TestCase(8000, 1, 20.0)]
  [TestCase(12000, 1, 10.0)]
  [TestCase(16000, 2, 40.0)]
  [TestCase(24000, 2, 60.0)]
  [TestCase(48000, 1, 2.5)]
  [TestCase(48000, 2, 5.0)]
  public void Encode_AllRatesAndFrameDurations_ProduceValidOggOpus(int sampleRate, int channels, double frameMs) {
    var pcm = BuildSignal(sampleRate / 5, channels, sampleRate);
    var encoded = OpusCodec.Encode(pcm, new OpusEncoderOptions(
      sampleRate,
      channels,
      Bitrate: channels == 1 ? 32000 : 64000,
      Complexity: 5,
      UseVbr: true,
      FrameDurationMilliseconds: frameMs,
      SerialNumber: 0x10203040,
      Vendor: "CompressionWorkbench tests"));

    using var stream = new MemoryStream(encoded, writable: false);
    var info = OpusCodec.ReadStreamInfo(stream);
    Assert.Multiple(() => {
      Assert.That(encoded.AsSpan(0, 4).ToArray(), Is.EqualTo("OggS"u8.ToArray()));
      Assert.That(info.SampleRate, Is.EqualTo(48000));
      Assert.That(info.InputSampleRate, Is.EqualTo(sampleRate));
      Assert.That(info.Channels, Is.EqualTo(channels));
      Assert.That(info.PreSkip, Is.GreaterThan(0));
      Assert.That(info.Vendor, Is.EqualTo("CompressionWorkbench tests"));
    });
  }

  [TestCase(false, false)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public void Encode_CbrVbrAndConstrainedVbr_AreDecodable(bool vbr, bool constrained) {
    var pcm = BuildSignal(9600, 2, 48000);
    var encoded = OpusCodec.Encode(pcm, new OpusEncoderOptions(
      48000, 2,
      Bitrate: 96000,
      UseVbr: vbr,
      ConstrainedVbr: constrained,
      Complexity: 10,
      FrameDurationMilliseconds: 20));

    var decoded = Decode48k(encoded);
    Assert.That(decoded.Length, Is.GreaterThanOrEqualTo(pcm.Length));
    Assert.That(SignalCorrelation(pcm, decoded.AsSpan(0, pcm.Length)), Is.GreaterThan(0.70));
  }

  [Test]
  public void Encode_LowerBitrate_ProducesSmallerStreamForCbr() {
    var pcm = BuildSignal(48000, 1, 48000);
    var low = OpusCodec.Encode(pcm, new OpusEncoderOptions(48000, 1,
      Bitrate: 24000, UseVbr: false, FrameDurationMilliseconds: 20));
    var high = OpusCodec.Encode(pcm, new OpusEncoderOptions(48000, 1,
      Bitrate: 96000, UseVbr: false, FrameDurationMilliseconds: 20));

    Assert.That(low.Length, Is.LessThan(high.Length));
    Assert.That(SignalCorrelation(pcm, Decode48k(low).AsSpan(0, pcm.Length)), Is.GreaterThan(0.55));
    Assert.That(SignalCorrelation(pcm, Decode48k(high).AsSpan(0, pcm.Length)), Is.GreaterThan(0.80));
  }

  [Test]
  public void Encode_ExposesVoiceFecDtxBandwidthAndModeControls() {
    var pcm = BuildSignal(16000, 1, 16000);
    var encoded = OpusCodec.Encode(pcm, new OpusEncoderOptions(
      16000, 1,
      Application: OpusApplication.OPUS_APPLICATION_VOIP,
      Bitrate: 18000,
      Complexity: 3,
      UseVbr: true,
      UseDtx: true,
      UseInbandFec: true,
      PacketLossPercent: 12,
      ForceChannels: 1,
      MaxBandwidth: OpusBandwidth.OPUS_BANDWIDTH_WIDEBAND,
      Bandwidth: OpusBandwidth.OPUS_BANDWIDTH_WIDEBAND,
      Signal: OpusSignal.OPUS_SIGNAL_VOICE,
      ForceMode: ConcentusOpusMode.MODE_SILK_ONLY,
      PredictionDisabled: false,
      LsbDepth: 16,
      FrameDurationMilliseconds: 20));

    using var stream = new MemoryStream(encoded, writable: false);
    var info = OpusCodec.ReadStreamInfo(stream);
    Assert.That(info.InputSampleRate, Is.EqualTo(16000));
    Assert.That(encoded.Length, Is.GreaterThan(100));
  }

  [TestCase(6000)]
  [TestCase(600000)]
  public void Encode_RejectsOutOfRangeBitrate(int bitrate) {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      OpusCodec.Encode(new short[960], new OpusEncoderOptions(48000, 1, Bitrate: bitrate)));
  }

  private static short[] BuildSignal(int frames, int channels, int sampleRate) {
    var result = new short[frames * channels];
    for (var frame = 0; frame < frames; ++frame)
      for (var c = 0; c < channels; ++c) {
        var tone = Math.Sin(2 * Math.PI * (260 + c * 190) * frame / sampleRate) * 11000;
        var overtone = Math.Sin(2 * Math.PI * (780 + c * 130) * frame / sampleRate) * 3500;
        result[frame * channels + c] = (short)(tone + overtone);
      }
    return result;
  }

  private static short[] Decode48k(byte[] opus) {
    using var input = new MemoryStream(opus, writable: false);
    using var output = new MemoryStream();
    OpusCodec.Decompress(input, output);
    var bytes = output.ToArray();
    var result = new short[bytes.Length / 2];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8);
    return result;
  }

  private static double SignalCorrelation(ReadOnlySpan<short> expected, ReadOnlySpan<short> actual) {
    Assert.That(actual.Length, Is.GreaterThanOrEqualTo(expected.Length));
    double ex = 0, ac = 0, ee = 0, aa = 0;
    for (var i = 0; i < expected.Length; ++i) {
      ex += expected[i] * (double)actual[i];
      ee += expected[i] * (double)expected[i];
      aa += actual[i] * (double)actual[i];
    }
    return ex / Math.Sqrt(ee * aa);
  }
}
