using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Ac3;
using FileFormat.Dts;
using FileFormat.Flac;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class ElementaryAudioConversionTests {

  [Test]
  public void WavToAc3ToFlac_PreservesGeometryAndSignal() {
    const int sampleRate = 48_000;
    const int channels = 2;
    var pcm = BuildPcm16(sampleRate, channels, 3_072);
    var wav = PcmCodec.ToWavBlob(pcm, channels, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var ac3 = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      ac3,
      new Ac3FormatDescriptor(),
      new FormatCreateOptions(Method: "ac3") {
        FormatSpecific = { ["bitrate"] = "192000", ["dialnorm"] = "-27" },
      });

    var encoded = ac3.ToArray();
    Assert.That(encoded.AsSpan(0, 2).ToArray(), Is.EqualTo(new byte[] { 0x0B, 0x77 }));

    ac3.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      ac3,
      new Ac3FormatDescriptor(),
      flac,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac"));

    flac.Position = 0;
    var decoded = new FlacFormatDescriptor().DecodePcm(flac);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.Format.Channels, Is.EqualTo(channels));
      Assert.That(decoded.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.InterleavedData.Length, Is.GreaterThanOrEqualTo(pcm.Length));
      Assert.That(decoded.InterleavedData.Any(static value => value != 0), Is.True);
    });
  }

  [Test]
  public void WavToDtsToFlac_PreservesGeometryAndSignal() {
    const int sampleRate = 48_000;
    const int channels = 2;
    var pcm = BuildPcm16(sampleRate, channels, 2_048);
    var wav = PcmCodec.ToWavBlob(pcm, channels, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var dts = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      dts,
      new DtsFormatDescriptor(),
      new FormatCreateOptions(Method: "dts") {
        FormatSpecific = { ["bitrate"] = "768000", ["subbands"] = "16" },
      });

    var encoded = dts.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(encoded), Is.EqualTo(0x7FFE8001u));

    dts.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      dts,
      new DtsFormatDescriptor(),
      flac,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac"));

    flac.Position = 0;
    var decoded = new FlacFormatDescriptor().DecodePcm(flac);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(sampleRate));
      Assert.That(decoded.Format.Channels, Is.EqualTo(channels));
      Assert.That(decoded.Format.BitsPerSample, Is.EqualTo(16));
      Assert.That(decoded.InterleavedData.Length, Is.GreaterThanOrEqualTo(pcm.Length));
      Assert.That(decoded.InterleavedData.Any(static value => value != 0), Is.True);
    });
  }

  private static byte[] BuildPcm16(int sampleRate, int channels, int frames) {
    var pcm = new byte[frames * channels * 2];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var sample = (short)Math.Round(Math.Sin(2.0 * Math.PI * (440.0 + channel * 110.0) * frame / sampleRate) * 9_000.0);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((frame * channels + channel) * 2, 2), sample);
      }
    return pcm;
  }
}
