#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Text;
using Codec.MonkeysAudio;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Ape;

namespace Compression.Tests.Ape;

[TestFixture]
public sealed class ApeWriteRemuxTests {

  private const int SampleRate = 44_100;

  private static IEnumerable<TestCaseData> EncodeCombinations() {
    foreach (var level in new[] { 1000, 2000, 3000, 4000, 5000 })
      foreach (var bits in new[] { 8, 16, 24 })
        foreach (var channels in new[] { 1, 2 })
          yield return new TestCaseData(level, bits, channels)
            .SetName($"Encode_{level}_{bits}bit_{channels}ch");
  }

  private static FormatCreateOptions Options(int level) => new() {
    FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["CompressionLevel"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
    },
  };

  private static byte[] GeneratePcm(int bits, int channels, int frames) {
    var bytesPerSample = bits / 8;
    var pcm = new byte[checked(frames * channels * bytesPerSample)];
    for (var frame = 0; frame < frames; ++frame)
      for (var channel = 0; channel < channels; ++channel) {
        var position = (frame * channels + channel) * bytesPerSample;
        var seed = frame * 977 + channel * 811;
        switch (bits) {
          case 8:
            pcm[position] = (byte)(128 + ((seed % 181) - 90));
            break;
          case 16:
            BinaryPrimitives.WriteInt16LittleEndian(
              pcm.AsSpan(position, 2), (short)(((seed * 37) % 60_001) - 30_000));
            break;
          case 24: {
            var value = ((seed * 65_537) % 12_000_001) - 6_000_000;
            pcm[position] = (byte)value;
            pcm[position + 1] = (byte)(value >> 8);
            pcm[position + 2] = (byte)(value >> 16);
            break;
          }
        }
      }
    return pcm;
  }

  private static byte[] Encode(ApeFormatDescriptor descriptor, byte[] pcm, int bits, int channels, int level) {
    using var output = new MemoryStream();
    descriptor.EncodePcm(
      output,
      new AudioPcmBuffer(
        new AudioPcmFormat(
          SampleRate,
          channels,
          bits,
          bits == 8 ? AudioPcmEncoding.UnsignedInteger : AudioPcmEncoding.SignedInteger),
        pcm),
      "ape",
      Options(level));
    return output.ToArray();
  }

  [TestCaseSource(nameof(EncodeCombinations))]
  public void EncodeDecode_AllThirtyImplementedCombinations_AreLossless(int level, int bits, int channels) {
    var descriptor = new ApeFormatDescriptor();
    var pcm = GeneratePcm(bits, channels, frames: 64);
    var format = new AudioPcmFormat(
      SampleRate,
      channels,
      bits,
      bits == 8 ? AudioPcmEncoding.UnsignedInteger : AudioPcmEncoding.SignedInteger);

    Assert.That(descriptor.CanEncode(format, "ape", Options(level), out var reason), Is.True, reason);
    var encoded = Encode(descriptor, pcm, bits, channels, level);

    using var probe = new MemoryStream(encoded, writable: false);
    var info = MonkeysAudioCodec.ReadStreamInfo(probe);
    Assert.Multiple(() => {
      Assert.That(info.CompressionLevel, Is.EqualTo(level));
      Assert.That(info.BitsPerSample, Is.EqualTo(bits));
      Assert.That(info.Channels, Is.EqualTo(channels));
    });

    using var source = new MemoryStream(encoded, writable: false);
    var decoded = descriptor.DecodePcm(source);
    Assert.Multiple(() => {
      Assert.That(decoded.Format.SampleRate, Is.EqualTo(SampleRate));
      Assert.That(decoded.Format.BitsPerSample, Is.EqualTo(bits));
      Assert.That(decoded.Format.Channels, Is.EqualTo(channels));
      Assert.That(decoded.InterleavedData, Is.EqualTo(pcm));
    });
  }

  [Test]
  public void Create_FromChannelWavs_UsesRequestedLevelAndPreservesPcm() {
    var descriptor = new ApeFormatDescriptor();
    var pcm = GeneratePcm(24, 2, 257);
    var channels = PcmCodec.SplitInterleavedPcm(pcm, 2, SampleRate, 24);
    var inputs = channels
      .Select(static channel => ArchiveInputInfo.InMemory($"{channel.Name}.wav", channel.WavBlob))
      .ToArray();

    using var output = new MemoryStream();
    descriptor.Create(output, inputs, Options(5000));
    var encoded = output.ToArray();

    using var probe = new MemoryStream(encoded, writable: false);
    Assert.That(MonkeysAudioCodec.ReadStreamInfo(probe).CompressionLevel, Is.EqualTo(5000));
    using var source = new MemoryStream(encoded, writable: false);
    Assert.That(descriptor.DecodePcm(source).InterleavedData, Is.EqualTo(pcm));
  }

  [Test]
  public void DemuxMux_Unchanged_PreservesLeadingJunkTailTagsAndEveryByte() {
    var descriptor = new ApeFormatDescriptor();
    var pcm = GeneratePcm(16, 2, 1024);
    var encoded = Encode(descriptor, pcm, 16, 2, 3000);

    // APE_DESCRIPTOR.terminatingDataBytes is the preserved WAV tail, not the APEv2 tag.
    var tail = "TAIL"u8.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(32, 4), (uint)tail.Length);
    var tag = BuildApeV2Tag(("ARTIST", "CWB"), ("TITLE", "Packet remux"));
    var leading = "JUNK-before-MAC\0"u8.ToArray();
    var decorated = leading.Concat(encoded).Concat(tail).Concat(tag).ToArray();

    using var input = new MemoryStream(decorated, writable: false);
    Assert.That(descriptor.TryDemux(input, out var demuxed), Is.True);
    Assert.That(demuxed, Is.Not.Null);

    using var remuxed = new MemoryStream();
    descriptor.Mux(remuxed, demuxed!, new FormatCreateOptions());
    Assert.That(remuxed.ToArray(), Is.EqualTo(decorated), "unchanged APE->APE remux must be byte-identical");

    using var decode = new MemoryStream(decorated, writable: false);
    Assert.That(descriptor.DecodePcm(decode).InterleavedData, Is.EqualTo(pcm),
      "leading bytes and trailing metadata must not leak into the codec payload");

    using var listing = new MemoryStream(decorated, writable: false);
    var names = descriptor.List(listing, null).Select(static entry => entry.Name).ToArray();
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("leading.bin"));
      Assert.That(names, Does.Contain("terminating.bin"));
      Assert.That(names, Does.Contain("tags.ini"));
    });
  }

  [Test]
  public void Mux_AfterPacketChange_RebuildsHeaderSeekTableAndDecodesChangedStream() {
    var descriptor = new ApeFormatDescriptor();
    const int frames = 73_728 + 211;
    var pcm = GeneratePcm(8, 1, frames);
    var encoded = Encode(descriptor, pcm, 8, 1, 1000);

    using var input = new MemoryStream(encoded, writable: false);
    Assert.That(descriptor.TryDemux(input, out var demuxed), Is.True);
    Assert.That(demuxed!.Packets, Has.Count.EqualTo(2));

    var shortened = demuxed with { Packets = demuxed.Packets.Take(1).ToArray() };
    using var rebuilt = new MemoryStream();
    descriptor.Mux(rebuilt, shortened, new FormatCreateOptions());
    var bytes = rebuilt.ToArray();
    Assert.That(bytes, Is.Not.EqualTo(encoded));

    using var probe = new MemoryStream(bytes, writable: false);
    var info = MonkeysAudioCodec.ReadStreamInfo(probe);
    Assert.That(info.TotalSamples, Is.EqualTo(73_728));

    using var decode = new MemoryStream(bytes, writable: false);
    var decoded = descriptor.DecodePcm(decode);
    Assert.That(decoded.InterleavedData, Is.EqualTo(pcm.AsSpan(0, 73_728).ToArray()));

    using var list = new MemoryStream(bytes, writable: false);
    Assert.That(descriptor.List(list, null).Count(static entry => entry.Kind == "Frame"), Is.EqualTo(1));
  }

  [Test]
  public void Demux_HonorsAudioDataHighWord_InsteadOfTruncatingTo32Bits() {
    var descriptor = new ApeFormatDescriptor();
    var encoded = Encode(descriptor, GeneratePcm(16, 1, 32), 16, 1, 1000);
    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(28, 4), 1);

    using var input = new MemoryStream(encoded, writable: false);
    Assert.That(descriptor.TryDemux(input, out _), Is.False,
      "a declared 4GiB+ frame-data region cannot fit in this small buffer; ignoring the high word would accept it");
  }

  [Test]
  public void PassThroughCreate_FullApe_IsByteIdentical() {
    var descriptor = new ApeFormatDescriptor();
    var encoded = Encode(descriptor, GeneratePcm(16, 1, 32), 16, 1, 2000);
    using var output = new MemoryStream();
    descriptor.Create(
      output,
      [ArchiveInputInfo.InMemory("FULL.ape", encoded)],
      new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(encoded));
  }

  private static byte[] BuildApeV2Tag(params (string Key, string Value)[] items) {
    using var body = new MemoryStream();
    Span<byte> u32 = stackalloc byte[4];
    foreach (var (key, value) in items) {
      var valueBytes = Encoding.UTF8.GetBytes(value);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)valueBytes.Length);
      body.Write(u32);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, 0);
      body.Write(u32);
      body.Write(Encoding.ASCII.GetBytes(key));
      body.WriteByte(0);
      body.Write(valueBytes);
    }

    var itemBytes = body.ToArray();
    var tagSize = checked((uint)(itemBytes.Length + ApeTagReader.DescriptorSize));
    using var tag = new MemoryStream();
    tag.Write(itemBytes);
    tag.Write("APETAGEX"u8);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 2000);
    tag.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, tagSize);
    tag.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)items.Length);
    tag.Write(u32);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, 0);
    tag.Write(u32);
    tag.Write(new byte[8]);
    return tag.ToArray();
  }
}
