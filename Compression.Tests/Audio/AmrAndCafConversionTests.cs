using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.AmrNb;
using FileFormat.AmrWb;
using FileFormat.Caf;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AmrAndCafConversionTests {

  [Test]
  public void AmrNb_Descriptor_EncodesDemuxesAndDecodesStorageFrames() {
    const int frames = 173;
    var pcm = BuildPcm16(8_000, 1, frames);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 8_000, 16);
    var descriptor = new AmrNbFormatDescriptor();

    using var input = new MemoryStream(wav, writable: false);
    using var amr = new MemoryStream();
    AudioConversionOperation.Convert(input, new WavFormatDescriptor(), amr, descriptor,
      new FormatCreateOptions(Method: "amr-nb"));

    var bytes = amr.ToArray();
    Assert.That(bytes.AsSpan(0, 6).SequenceEqual("#!AMR\n"u8), Is.True);

    amr.Position = 0;
    Assert.That(descriptor.TryDemux(amr, out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(encoded!.Format.CodecId, Is.EqualTo("amr-nb"));
      Assert.That(encoded.Packets.Count, Is.EqualTo(2));
      Assert.That(encoded.Packets.All(static packet => packet.DurationSamples == 160), Is.True);
    });

    amr.Position = 0;
    var decoded = descriptor.DecodePcm(amr);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(8_000));
      Assert.That(decoded.Format.Channels, Is.EqualTo(1));
      Assert.That(decoded.InterleavedData.Length, Is.EqualTo(2 * 160 * 2));
    });

    using var truncated = new MemoryStream(bytes[..^1], writable: false);
    Assert.That(descriptor.TryDemux(truncated, out _), Is.False);
  }

  [Test]
  public void AmrWb_Descriptor_EncodesDemuxesAndDecodesStorageFrames() {
    const int frames = 333;
    var pcm = BuildPcm16(16_000, 1, frames);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 16_000, 16);
    var descriptor = new AmrWbFormatDescriptor();

    using var input = new MemoryStream(wav, writable: false);
    using var amr = new MemoryStream();
    AudioConversionOperation.Convert(input, new WavFormatDescriptor(), amr, descriptor,
      new FormatCreateOptions(Method: "amr-wb"));

    var bytes = amr.ToArray();
    Assert.That(bytes.AsSpan(0, 9).SequenceEqual("#!AMR-WB\n"u8), Is.True);

    amr.Position = 0;
    Assert.That(descriptor.TryDemux(amr, out var encoded), Is.True);
    Assert.That(encoded, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(encoded!.Format.CodecId, Is.EqualTo("amr-wb"));
      Assert.That(encoded.Packets.Count, Is.EqualTo(2));
      Assert.That(encoded.Packets.All(static packet => packet.DurationSamples == 320), Is.True);
    });

    amr.Position = 0;
    var decoded = descriptor.DecodePcm(amr);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(16_000));
      Assert.That(decoded.Format.Channels, Is.EqualTo(1));
      Assert.That(decoded.InterleavedData.Length, Is.EqualTo(2 * 320 * 2));
    });

    using var truncated = new MemoryStream(bytes[..^1], writable: false);
    Assert.That(descriptor.TryDemux(truncated, out _), Is.False);
  }

  [Test]
  public void CafIma4_PaktPreservesOriginalValidFrameCount() {
    const int sampleRate = 44_100;
    const int channels = 2;
    const int frames = 130;
    var pcm = BuildPcm16(sampleRate, channels, frames);
    var wav = PcmCodec.ToWavBlob(pcm, channels, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var caf = new MemoryStream();
    AudioConversionOperation.Convert(input, new WavFormatDescriptor(), caf, new CafFormatDescriptor(),
      new FormatCreateOptions(Method: "ima4"));

    var parsed = new CafReader().Read(caf.ToArray());
    Assert.Multiple(() => {
      Assert.That(parsed.FormatId, Is.EqualTo("lpcm"));
      Assert.That(parsed.ValidFrames, Is.EqualTo(frames));
      Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
      Assert.That(parsed.NumChannels, Is.EqualTo(channels));
      Assert.That(parsed.InterleavedPcm.Length, Is.EqualTo(frames * channels * 2));
      Assert.That(parsed.InterleavedPcm.Any(static value => value != 0), Is.True);
    });
  }

  private static byte[] BuildPcm16(int sampleRate, int channels, int frames) {
    var pcm = new byte[frames * channels * 2];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var sample = (short)Math.Round(Math.Sin(2 * Math.PI * (310 + channel * 170) * frame / sampleRate) * 12_000);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((frame * channels + channel) * 2, 2), sample);
      }
    return pcm;
  }
}
