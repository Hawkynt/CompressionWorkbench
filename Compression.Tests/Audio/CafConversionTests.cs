using Codec.Pcm;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Caf;
using FileFormat.Flac;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class CafConversionTests {

  [Test]
  public void WavToCafLpcmToFlac_IsLosslessAndUsesStandardFlags() {
    const int sampleRate = 48_000;
    var pcm = BuildPcm16(sampleRate, 2, 2_048);
    var wav = PcmCodec.ToWavBlob(pcm, 2, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var caf = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      caf,
      new CafFormatDescriptor(),
      new FormatCreateOptions(Method: "lpcm"));

    var parsed = new CafReader().Read(caf.ToArray());
    Assert.Multiple(() => {
      Assert.That(parsed.FormatId, Is.EqualTo("lpcm"));
      Assert.That(parsed.FormatFlags & 0x2u, Is.Zero, "little-endian flag stays clear: CAF LPCM ships big-endian");
      Assert.That(parsed.FormatFlags & 0x4u, Is.Not.Zero, "signed-integer flag");
      Assert.That(parsed.FormatFlags & 0x8u, Is.Not.Zero, "packed flag");
      Assert.That(parsed.InterleavedPcm, Is.EqualTo(pcm));
    });

    caf.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      caf,
      new CafFormatDescriptor(),
      flac,
      new FlacFormatDescriptor(),
      new FormatCreateOptions(Method: "flac"));

    flac.Position = 0;
    var decoded = new FlacFormatDescriptor().DecodePcm(flac);
    Assert.That(decoded.InterleavedData, Is.EqualTo(pcm));
  }

  [TestCase("mulaw")]
  [TestCase("alaw")]
  public void WavToCafG711ToFlac_PreservesGeometryAndSignal(string codec) {
    const int sampleRate = 8_000;
    var pcm = BuildPcm16(sampleRate, 1, 1_600);
    var wav = PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16);

    using var input = new MemoryStream(wav, writable: false);
    using var caf = new MemoryStream();
    AudioConversionOperation.Convert(
      input,
      new WavFormatDescriptor(),
      caf,
      new CafFormatDescriptor(),
      new FormatCreateOptions(Method: codec));

    caf.Position = 0;
    using var flac = new MemoryStream();
    AudioConversionOperation.Convert(
      caf,
      new CafFormatDescriptor(),
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
        var value = (short)Math.Round(Math.Sin(2.0 * Math.PI * (280.0 + channel * 150.0) * frame / sampleRate) * 11_000.0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
          pcm.AsSpan((frame * channels + channel) * 2, 2), value);
      }
    return pcm;
  }
}
