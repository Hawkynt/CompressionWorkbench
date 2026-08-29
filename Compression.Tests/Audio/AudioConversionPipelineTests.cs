using Codec.Aac;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Aac;
using FileFormat.Flac;
using FileFormat.Mp4;
using FileFormat.Ogg;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AudioConversionPipelineTests {

  [Test]
  public void SameFormat_IsByteExactPassthrough() {
    var inputBytes = BuildAac(sampleRate: 44_100, channels: 1, frames: 2_048);
    using var input = new MemoryStream(inputBytes, writable: false);
    using var output = new MemoryStream();

    var descriptor = new AacFormatDescriptor();
    AudioConversionOperation.Convert(input, descriptor, output, descriptor);

    Assert.That(output.ToArray(), Is.EqualTo(inputBytes));
  }

  [Test]
  public void AacToM4a_RemuxPreservesEveryAccessUnit() {
    var adts = BuildAac(sampleRate: 44_100, channels: 2, frames: 3_000);
    var aac = new AacFormatDescriptor();
    using var packetSource = new MemoryStream(adts, writable: false);
    Assert.That(aac.TryDemux(packetSource, out var elementary), Is.True);
    Assert.That(elementary, Is.Not.Null);

    using var input = new MemoryStream(adts, writable: false);
    using var output = new MemoryStream();
    AudioConversionOperation.Convert(input, aac, output, new Mp4FormatDescriptor());

    var tracks = new Mp4Demuxer().Demux(output.ToArray());
    var audio = tracks.Single(track => track.HandlerType == "soun");
    Assert.That(audio.CodecFourCc, Is.EqualTo("mp4a"));
    Assert.That(audio.Samples.Count, Is.EqualTo(elementary!.Packets.Count));
    for (var i = 0; i < audio.Samples.Count; ++i)
      Assert.That(audio.Samples[i].Data, Is.EqualTo(elementary.Packets[i].Data), $"AAC access unit {i}");
  }

  [Test]
  public void WavToFlac_IsLosslessThroughCommonPipeline() {
    var pcm = BuildPcm16(44_100, channels: 2, frames: 4_096);
    var wav = PcmCodec.ToWavBlob(pcm, 2, 44_100, 16);
    using var input = new MemoryStream(wav, writable: false);
    using var encoded = new MemoryStream();

    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      encoded,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac") {
        FormatSpecific = { ["block-size"] = "1024", ["stereo-mode"] = "mid-side" },
      });

    encoded.Position = 0;
    var flac = new FlacFormatDescriptor().DecodePcm(encoded);
    Assert.Multiple(() => {
      Assert.That(flac.Format.SampleRate, Is.EqualTo(44_100));
      Assert.That(flac.Format.Channels, Is.EqualTo(2));
      Assert.That(flac.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(flac.InterleavedData, Is.EqualTo(pcm));
    });
  }

  [TestCase("vorbis")]
  [TestCase("opus")]
  public void WavToOgg_UsesSelectedManagedEncoder(string codec) {
    const int sampleRate = 48_000;
    var pcm = BuildPcm16(sampleRate, channels: 2, frames: 2_400);
    var wav = PcmCodec.ToWavBlob(pcm, 2, sampleRate, 16);
    using var input = new MemoryStream(wav, writable: false);
    using var encoded = new MemoryStream();

    var options = new FormatCreateOptions(Method: codec);
    if (codec == "vorbis") options.FormatSpecific["quality"] = "0.35";
    else options.FormatSpecific["bitrate"] = "96000";

    AudioConversionOperation.Convert(input, new WavFormatDescriptor(), encoded, new OggFormatDescriptor(), options);

    var bytes = encoded.ToArray();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("OggS"u8.ToArray()));
    using var ogg = new MemoryStream(bytes, writable: false);
    var decoded = new OggFormatDescriptor().DecodePcm(ogg);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.Channels, Is.EqualTo(2));
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.InterleavedData.Length, Is.GreaterThan(0));
      Assert.That(decoded.InterleavedData.Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void WavToM4a_EncodesAndDecodesThroughExistingMp4AudioSurface() {
    const int sampleRate = 44_100;
    var pcm = BuildPcm16(sampleRate, channels: 1, frames: 2_048);
    var wav = PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16);
    using var input = new MemoryStream(wav, writable: false);
    using var output = new MemoryStream();

    var options = new FormatCreateOptions(Method: "aac");
    options.FormatSpecific["bitrate"] = "64000";
    AudioConversionOperation.Convert(input, new WavFormatDescriptor(), output, new Mp4FormatDescriptor(), options);

    var bytes = output.ToArray();
    var boxes = new BoxParser().Parse(bytes);
    Assert.That(boxes.Select(static box => box.Type), Is.EqualTo(new[] { "ftyp", "mdat", "moov" }));
    var tracks = new Mp4Demuxer().Demux(bytes);
    Assert.That(tracks.Count(static track => track.HandlerType == "soun"), Is.EqualTo(1));
    Assert.That(tracks.Single(static track => track.HandlerType == "soun").Samples.Count, Is.GreaterThan(0));
  }

  [Test]
  public void UnsupportedTargetCodec_FailsExplicitly() {
    var pcm = BuildPcm16(44_100, channels: 1, frames: 1_024);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 44_100, 16);
    using var input = new MemoryStream(wav, writable: false);
    using var output = new MemoryStream();

    var exception = Assert.Throws<NotSupportedException>(() =>
      AudioConversionOperation.Convert(
        input,
        new WavFormatDescriptor(),
        output,
        new OggFormatDescriptor(),
        new FormatCreateOptions(Method: "flac")));

    Assert.That(exception!.Message, Does.Contain("flac"));
  }

  private static byte[] BuildAac(int sampleRate, int channels, int frames) {
    var pcmBytes = BuildPcm16(sampleRate, channels, frames);
    var samples = new short[pcmBytes.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.AsSpan(i * 2, 2));
    return AacEncoder.Encode(samples, new AacEncoderOptions(sampleRate, channels, channels == 1 ? 64_000 : 128_000));
  }

  private static byte[] BuildPcm16(int sampleRate, int channels, int frames) {
    var pcm = new byte[frames * channels * 2];
    for (var frame = 0; frame < frames; ++frame) {
      for (var channel = 0; channel < channels; ++channel) {
        var frequency = 330.0 + channel * 220.0;
        var value = (short)Math.Round(Math.Sin(2.0 * Math.PI * frequency * frame / sampleRate) * 12_000.0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
          pcm.AsSpan((frame * channels + channel) * 2, 2), value);
      }
    }
    return pcm;
  }
}
