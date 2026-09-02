using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Aiff;
using FileFormat.Au;
using FileFormat.Flac;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AudioContainerAdapterTests {

  [Test]
  public void WavToAuPcmToFlac_IsLossless() {
    const int sampleRate = 48_000;
    var pcm = BuildPcm16(sampleRate, channels: 2, frames: 2_048);
    var wav = PcmCodec.ToWavBlob(pcm, 2, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var au = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      au,
      new AuFormatDescriptor(),
      new FormatCreateOptions(Method: "pcm"));

    Assert.That(au.ToArray().AsSpan(0, 4).ToArray(), Is.EqualTo(".snd"u8.ToArray()));

    au.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      au,
      new AuFormatDescriptor(),
      flac,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac"));

    flac.Position = 0;
    var decoded = new FlacFormatDescriptor().DecodePcm(flac);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.Format.Channels, Is.EqualTo(2));
      Assert.That(decoded.InterleavedData, Is.EqualTo(pcm));
    });
  }

  [TestCase("mulaw", 1u)]
  [TestCase("alaw", 27u)]
  public void WavToAuG711ToFlac_PreservesGeometryAndSignal(string codec, uint expectedEncoding) {
    const int sampleRate = 8_000;
    var pcm = BuildPcm16(sampleRate, channels: 1, frames: 1_600);
    var wav = PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var au = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      au,
      new AuFormatDescriptor(),
      new FormatCreateOptions(Method: codec));

    var parsed = new AuReader().Read(au.ToArray());
    Assert.That(parsed.Encoding, Is.EqualTo(expectedEncoding));

    au.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      au,
      new AuFormatDescriptor(),
      flac,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac"));

    flac.Position = 0;
    var decoded = new FlacFormatDescriptor().DecodePcm(flac);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.Format.Channels, Is.EqualTo(1));
      Assert.That(decoded.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.InterleavedData.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.InterleavedData.Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void WavToAiffPcmToFlac_IsLossless() {
    const int sampleRate = 44_100;
    var pcm = BuildPcm16(sampleRate, channels: 2, frames: 2_048);
    var wav = PcmCodec.ToWavBlob(pcm, 2, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var aiff = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      aiff,
      new AiffFormatDescriptor(),
      new FormatCreateOptions(Method: "pcm"));

    var parsed = new AiffReader().Read(aiff.ToArray());
    Assert.Multiple(() => {
      Assert.That(parsed.IsAifc, Is.False);
      Assert.That(parsed.CompressionId, Is.EqualTo("NONE"));
      Assert.That(parsed.SampleFrames, Is.EqualTo(2_048));
    });

    aiff.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      aiff,
      new AiffFormatDescriptor(),
      flac,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac"));

    flac.Position = 0;
    var decoded = new FlacFormatDescriptor().DecodePcm(flac);
    Assert.That(decoded.InterleavedData, Is.EqualTo(pcm));
  }

  [Test]
  public void WavToAifcSowtToFlac_IsLossless() {
    const int sampleRate = 48_000;
    var pcm = BuildPcm16(sampleRate, channels: 2, frames: 1_536);
    var wav = PcmCodec.ToWavBlob(pcm, 2, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var aifc = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      aifc,
      new AiffFormatDescriptor(),
      new FormatCreateOptions(Method: "sowt"));

    var parsed = new AiffReader().Read(aifc.ToArray());
    Assert.Multiple(() => {
      Assert.That(parsed.IsAifc, Is.True);
      Assert.That(parsed.CompressionId, Is.EqualTo("sowt"));
      Assert.That(parsed.SampleFrames, Is.EqualTo(1_536));
    });

    aifc.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      aifc,
      new AiffFormatDescriptor(),
      flac,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac"));

    flac.Position = 0;
    var decoded = new FlacFormatDescriptor().DecodePcm(flac);
    Assert.That(decoded.InterleavedData, Is.EqualTo(pcm));
  }

  [TestCase("mulaw", "ulaw")]
  [TestCase("alaw", "alaw")]
  [TestCase("ima4", "ima4")]
  public void WavToAifcLossyCodecToFlac_PreservesGeometryAndSignal(string codec, string expectedCompressionId) {
    const int sampleRate = 8_000;
    var pcm = BuildPcm16(sampleRate, channels: 1, frames: 1_600);
    var wav = PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var aifc = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      aifc,
      new AiffFormatDescriptor(),
      new FormatCreateOptions(Method: codec));

    var parsed = new AiffReader().Read(aifc.ToArray());
    Assert.Multiple(() => {
      Assert.That(parsed.IsAifc, Is.True);
      Assert.That(parsed.CompressionId, Is.EqualTo(expectedCompressionId));
      Assert.That(parsed.SampleFrames, Is.EqualTo(1_600));
    });

    aifc.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      aifc,
      new AiffFormatDescriptor(),
      flac,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac"));

    flac.Position = 0;
    var decoded = new FlacFormatDescriptor().DecodePcm(flac);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.Format.Channels, Is.EqualTo(1));
      Assert.That(decoded.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.InterleavedData.Length, Is.EqualTo(pcm.Length));
      Assert.That(decoded.InterleavedData.Any(static value => value != 0), Is.True);
    });
  }

  private static byte[] BuildPcm16(int sampleRate, int channels, int frames) {
    var pcm = new byte[frames * channels * 2];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var frequency = 220.0 + 110.0 * channel;
        var value = (short)Math.Round(Math.Sin(2.0 * Math.PI * frequency * frame / sampleRate) * 10_000.0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
          pcm.AsSpan((frame * channels + channel) * 2, 2), value);
      }
    return pcm;
  }
}
