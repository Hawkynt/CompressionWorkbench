using System.Buffers.Binary;
using Codec.Alac;
using Compression.Registry;
using FileFormat.Alac;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class AlacConversionTests {
  private const int SampleRate = 48_000;
  private const int Frames = 131;
  private const int PacketFrames = 64;

  private static IEnumerable<TestCaseData> ProfileMatrix() {
    foreach (var bits in new[] { 16, 20, 24, 32 })
      for (var channels = 1; channels <= 8; ++channels)
        yield return new TestCaseData(bits, channels)
          .SetName($"ALAC_{bits}bit_{channels}ch");
  }

  private static IEnumerable<TestCaseData> ContainerProfileMatrix() {
    foreach (var profile in ProfileMatrix()) {
      var arguments = profile.Arguments;
      foreach (var container in new[] { "M4A", "CAF" })
        yield return new TestCaseData(arguments[0], arguments[1], container)
          .SetName($"ALAC_{arguments[0]}bit_{arguments[1]}ch_{container}_RoundTrip");
    }
  }

  [TestCaseSource(nameof(ContainerProfileMatrix))]
  public void EncodeContainerDecode_AllDepthAndChannelCombinations_AreLossless(
      int bitsPerSample, int channels, string container) {
    var descriptor = new AlacFormatDescriptor();
    var pcm = BuildPcm(bitsPerSample, channels, Frames);
    var source = new AudioPcmBuffer(
      new AudioPcmFormat(SampleRate, channels, bitsPerSample, AudioPcmEncoding.SignedInteger),
      pcm);

    using var encoded = new MemoryStream();
    descriptor.EncodePcm(encoded, source, "alac", Options(container));

    encoded.Position = 0;
    var decoded = descriptor.DecodePcm(encoded);

    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(SampleRate));
      Assert.That(decoded.Format.Channels, Is.EqualTo(channels));
      Assert.That(decoded.Format.BitsPerSample, Is.EqualTo(bitsPerSample));
      Assert.That(decoded.Format.Encoding, Is.EqualTo(AudioPcmEncoding.SignedInteger));
      Assert.That(decoded.InterleavedData, Is.EqualTo(pcm));
    });
  }

  [TestCaseSource(nameof(ProfileMatrix))]
  public void Remux_M4aToCafToM4a_PreservesEveryEncodedPacket(int bitsPerSample, int channels) {
    var descriptor = new AlacFormatDescriptor();
    var pcm = BuildPcm(bitsPerSample, channels, Frames);
    var source = new AudioPcmBuffer(
      new AudioPcmFormat(SampleRate, channels, bitsPerSample, AudioPcmEncoding.SignedInteger),
      pcm);

    using var initialM4a = new MemoryStream();
    descriptor.EncodePcm(initialM4a, source, "alac", Options("M4A"));
    var fromM4a = Demux(descriptor, initialM4a);

    using var caf = new MemoryStream();
    descriptor.Mux(caf, fromM4a, Options("CAF"));
    var fromCaf = Demux(descriptor, caf);
    AssertEncodedStreamsEqual(fromM4a, fromCaf);

    using var remuxedM4a = new MemoryStream();
    descriptor.Mux(remuxedM4a, fromCaf, Options("M4A"));
    var roundTripped = Demux(descriptor, remuxedM4a);
    AssertEncodedStreamsEqual(fromM4a, roundTripped);
  }

  [Test]
  public void Encode_20BitNonCanonicalLowBits_IsRejectedInsteadOfTruncated() {
    var pcm = BuildPcm(20, 1, 4);
    pcm[0] |= 0x01;

    var exception = Assert.Throws<ArgumentException>(() =>
      AlacEncoder.Encode(pcm, 1, SampleRate, 20, PacketFrames));

    Assert.That(exception!.Message, Does.Contain("low four bits"));
  }

  private static AudioEncodedStream Demux(AlacFormatDescriptor descriptor, MemoryStream container) {
    container.Position = 0;
    Assert.That(descriptor.TryDemux(container, out var stream), Is.True);
    Assert.That(stream, Is.Not.Null);
    return stream!;
  }

  private static void AssertEncodedStreamsEqual(AudioEncodedStream expected, AudioEncodedStream actual) {
    Assert.Multiple(() => {
      Assert.That(actual.Format.CodecId, Is.EqualTo(expected.Format.CodecId));
      Assert.That(actual.Format.SampleRate, Is.EqualTo(expected.Format.SampleRate));
      Assert.That(actual.Format.Channels, Is.EqualTo(expected.Format.Channels));
      Assert.That(actual.Format.BitsPerSample, Is.EqualTo(expected.Format.BitsPerSample));
      Assert.That(actual.CodecPrivateData, Is.EqualTo(expected.CodecPrivateData));
      Assert.That(actual.Packets.Count, Is.EqualTo(expected.Packets.Count));
    });

    for (var index = 0; index < expected.Packets.Count; ++index) {
      var expectedPacket = expected.Packets[index];
      var actualPacket = actual.Packets[index];
      Assert.Multiple(() => {
        Assert.That(actualPacket.Data, Is.EqualTo(expectedPacket.Data), $"packet {index} payload");
        Assert.That(actualPacket.DurationSamples, Is.EqualTo(expectedPacket.DurationSamples), $"packet {index} duration");
        Assert.That(actualPacket.IsHeader, Is.EqualTo(expectedPacket.IsHeader), $"packet {index} header flag");
      });
    }
  }

  private static FormatCreateOptions Options(string container) {
    var result = new FormatCreateOptions();
    result.FormatSpecific["Container"] = container;
    result.FormatSpecific["FrameLength"] = PacketFrames.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return result;
  }

  private static byte[] BuildPcm(int bitsPerSample, int channels, int frames) {
    var bytesPerSample = (bitsPerSample + 7) / 8;
    var result = new byte[checked(frames * channels * bytesPerSample)];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var seed = unchecked(
          (uint)(frame + 1) * 0x9E37_79B9u ^
          (uint)(channel + 1) * 0x85EB_CA6Bu ^
          (uint)bitsPerSample * 0xC2B2_AE35u);
        var signed = unchecked((int)(seed * 1_664_525u + 1_013_904_223u));
        var value = bitsPerSample switch {
          16 => signed >> 16,
          20 => signed >> 12,
          24 => signed >> 8,
          32 => signed,
          _ => throw new ArgumentOutOfRangeException(nameof(bitsPerSample)),
        };
        WriteSample(result, (frame * channels + channel) * bytesPerSample, value, bitsPerSample);
      }
    return result;
  }

  private static void WriteSample(byte[] target, int offset, int value, int bitsPerSample) {
    switch (bitsPerSample) {
      case 16:
        BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset, 2), checked((short)value));
        break;
      case 20:
        WriteSigned24(target, offset, value << 4);
        break;
      case 24:
        WriteSigned24(target, offset, value);
        break;
      case 32:
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
    }
  }

  private static void WriteSigned24(byte[] target, int offset, int value) {
    target[offset] = (byte)value;
    target[offset + 1] = (byte)(value >> 8);
    target[offset + 2] = (byte)(value >> 16);
  }
}
