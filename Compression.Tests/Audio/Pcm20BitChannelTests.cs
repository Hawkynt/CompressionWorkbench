using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Alac;
using FileFormat.Mp4;
using FileFormat.Wav;
using NUnit.Framework;

namespace Compression.Tests.Audio;

[TestFixture]
public sealed class Pcm20BitChannelTests {
  private const int SampleRate = 48_000;

  [Test]
  public void SplitAndInterleave_20BitPcm_UsesThreeByteSampleStorage() {
    var original = BuildStereoPcm();

    var split = PcmCodec.SplitInterleavedPcm(original, channels: 2, SampleRate, bitsPerSample: 20);
    Assert.That(split, Has.Count.EqualTo(2));

    var left = new WavReader().Read(split[0].WavBlob);
    var right = new WavReader().Read(split[1].WavBlob);

    Assert.Multiple(() => {
      Assert.That(left.NumChannels, Is.EqualTo(1));
      Assert.That(right.NumChannels, Is.EqualTo(1));
      Assert.That(left.BitsPerSample, Is.EqualTo(20));
      Assert.That(right.BitsPerSample, Is.EqualTo(20));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(split[0].WavBlob.AsSpan(28, 4)), Is.EqualTo((uint)(SampleRate * 3)));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(split[0].WavBlob.AsSpan(32, 2)), Is.EqualTo((ushort)3));
      Assert.That(left.InterleavedPcm, Is.EqualTo(Deinterleave(original, channel: 0)));
      Assert.That(right.InterleavedPcm, Is.EqualTo(Deinterleave(original, channel: 1)));
    });

    Assert.That(
      PcmCodec.Interleave([left.InterleavedPcm, right.InterleavedPcm], bitsPerSample: 20),
      Is.EqualTo(original));

    var remuxed = WavChannelMux.Interleave([
      ("FRONT_LEFT.wav", split[0].WavBlob),
      ("FRONT_RIGHT.wav", split[1].WavBlob),
    ]);
    Assert.Multiple(() => {
      Assert.That(remuxed.BitsPerSample, Is.EqualTo(20));
      Assert.That(remuxed.Channels, Is.EqualTo(2));
      Assert.That(remuxed.Interleaved, Is.EqualTo(original));
    });
  }

  [Test]
  public void Mp4AlacChannelView_20Bit_PreservesThreeByteSamplesPerChannel() {
    var original = BuildStereoPcm();
    var source = new AudioPcmBuffer(
      new AudioPcmFormat(SampleRate, 2, 20, AudioPcmEncoding.SignedInteger),
      original);
    var options = new FormatCreateOptions();
    options.FormatSpecific["Container"] = "M4A";
    options.FormatSpecific["FrameLength"] = "2";

    using var m4a = new MemoryStream();
    new AlacFormatDescriptor().EncodePcm(m4a, source, "alac", options);

    var mp4 = new Mp4FormatDescriptor();
    m4a.Position = 0;
    var channels = mp4.List(m4a, null)
      .Where(static entry => entry.Kind == "Channel")
      .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
      .ToArray();

    Assert.That(channels.Select(static entry => entry.Name), Is.EqualTo(new[] {
      "TRACK0_FRONT_LEFT.wav",
      "TRACK0_FRONT_RIGHT.wav",
    }));

    for (var channel = 0; channel < channels.Length; ++channel) {
      using var extracted = new MemoryStream();
      m4a.Position = 0;
      mp4.ExtractEntry(m4a, channels[channel].Name, extracted, null);
      var parsed = new WavReader().Read(extracted.ToArray());

      Assert.Multiple(() => {
        Assert.That(parsed.BitsPerSample, Is.EqualTo(20));
        Assert.That(parsed.NumChannels, Is.EqualTo(1));
        Assert.That(parsed.InterleavedPcm, Is.EqualTo(Deinterleave(original, channel)));
      });
    }
  }

  private static byte[] BuildStereoPcm() => [
    0x10, 0x32, 0x54,  0x20, 0x76, 0x98,
    0x30, 0xBA, 0xDC,  0x40, 0xFE, 0x10,
    0x50, 0x21, 0x43,  0x60, 0x65, 0x87,
    0x70, 0xA9, 0xCB,  0x80, 0xED, 0x0F,
  ];

  private static byte[] Deinterleave(byte[] interleaved, int channel) {
    const int bytesPerSample = 3;
    const int channels = 2;
    var frames = interleaved.Length / (bytesPerSample * channels);
    var result = new byte[frames * bytesPerSample];
    for (var frame = 0; frame < frames; ++frame)
      Buffer.BlockCopy(
        interleaved,
        (frame * channels + channel) * bytesPerSample,
        result,
        frame * bytesPerSample,
        bytesPerSample);
    return result;
  }
}
